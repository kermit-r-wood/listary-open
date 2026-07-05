use serde::{Deserialize, Serialize};
use std::env;
use std::io;
use std::ptr::{null, null_mut};
use std::thread;
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_PIPE_CONNECTED, HANDLE, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{ReadFile, WriteFile, PIPE_ACCESS_DUPLEX};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};

const DEFAULT_PIPE_NAME: &str = "listary-open-hook-x64";
const IPC_VERSION: u32 = 1;
const BUFFER_SIZE: usize = 64 * 1024;

fn main() -> io::Result<()> {
    let pipe_name = pipe_name_from_args();
    let pipe_path = format!(r"\\.\pipe\{}", pipe_name);

    println!("ListaryOpen hook host started on pipe '{}'.", pipe_name);

    loop {
        let pipe = NamedPipeHandle::create(&pipe_path)?;
        if let Err(error) = connect_pipe(pipe.raw()) {
            eprintln!("Hook host connect failed: {error}");
            continue;
        }

        thread::spawn(move || {
            if let Err(error) = serve_connection(pipe) {
                eprintln!("Hook host connection failed: {error}");
            }
        });
    }
}

fn pipe_name_from_args() -> String {
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        if arg == "--pipe" {
            if let Some(pipe_name) = args.next() {
                return pipe_name;
            }
        }
    }

    DEFAULT_PIPE_NAME.to_string()
}

fn serve_connection(pipe: NamedPipeHandle) -> io::Result<()> {
    let response = match read_line(pipe.raw()) {
        Ok(request) => response_for_request(&request),
        Err(_) => command_reply("Failed", "Unknown command."),
    };

    let result = write_line(pipe.raw(), &response);
    unsafe {
        DisconnectNamedPipe(pipe.raw());
    }

    result
}

fn connect_pipe(handle: HANDLE) -> io::Result<()> {
    let connected = unsafe { ConnectNamedPipe(handle, null_mut()) };
    if connected == 0 {
        let error = unsafe { GetLastError() };
        if error != ERROR_PIPE_CONNECTED {
            return Err(io::Error::from_raw_os_error(error as i32));
        }
    }

    Ok(())
}

fn read_line(handle: HANDLE) -> io::Result<String> {
    let mut request = Vec::new();
    let mut buffer = [0u8; 4096];
    let mut saw_newline = false;

    while request.len() < BUFFER_SIZE {
        let remaining = BUFFER_SIZE - request.len();
        let bytes_to_read = remaining.min(buffer.len()) as u32;
        let mut bytes_read = 0u32;
        let read = unsafe {
            ReadFile(
                handle,
                buffer.as_mut_ptr().cast(),
                bytes_to_read,
                &mut bytes_read,
                null_mut(),
            )
        };

        if read == 0 {
            let error = io::Error::last_os_error();
            if error.kind() == io::ErrorKind::BrokenPipe {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "hook IPC request ended before newline",
                ));
            }

            return Err(error);
        }

        if bytes_read == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "hook IPC request ended before newline",
            ));
        }

        let chunk = &buffer[..bytes_read as usize];
        if let Some(newline) = chunk.iter().position(|byte| *byte == b'\n') {
            request.extend_from_slice(&chunk[..newline]);
            saw_newline = true;
            break;
        }

        request.extend_from_slice(chunk);
    }

    if !saw_newline {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "hook IPC request exceeded maximum line length",
        ));
    }

    if request.ends_with(b"\r") {
        request.pop();
    }

    String::from_utf8(request).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

fn write_line(handle: HANDLE, line: &str) -> io::Result<()> {
    let mut response = line.as_bytes().to_vec();
    response.push(b'\n');

    let mut total_written = 0usize;
    while total_written < response.len() {
        let mut bytes_written = 0u32;
        let remaining = response.len() - total_written;
        let bytes_to_write = remaining.min(u32::MAX as usize) as u32;
        let written = unsafe {
            WriteFile(
                handle,
                response[total_written..].as_ptr().cast(),
                bytes_to_write,
                &mut bytes_written,
                null_mut(),
            )
        };

        if written == 0 {
            return Err(io::Error::last_os_error());
        }

        if bytes_written == 0 {
            return Err(io::Error::new(
                io::ErrorKind::WriteZero,
                "failed to write hook IPC response",
            ));
        }

        total_written += bytes_written as usize;
    }

    Ok(())
}

fn response_for_request(request: &str) -> String {
    let envelope = serde_json::from_str::<IncomingEnvelope>(request);
    let Ok(envelope) = envelope else {
        return command_reply("Failed", "Unknown command.");
    };

    if envelope.version != IPC_VERSION {
        return command_reply("Failed", "Unsupported IPC version.");
    }

    match envelope.message_type.as_str() {
        "JumpDialogToFolder" => command_reply("NoActiveDialog", "No active hook dialog."),
        "HealthProbe" => command_reply("Success", "Hook host healthy."),
        _ => command_reply("Failed", "Unknown command."),
    }
}

fn command_reply(status: &str, message: &str) -> String {
    let envelope = OutgoingEnvelope {
        version: IPC_VERSION,
        message_type: "CommandReply",
        payload: CommandReplyPayload { status, message },
    };

    serde_json::to_string(&envelope).expect("hook IPC command reply serialization should not fail")
}

fn to_wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

struct NamedPipeHandle(HANDLE);

// SAFETY: NamedPipeHandle owns one pipe HANDLE and ownership is moved to exactly
// one worker thread, which disconnects/closes it before drop.
unsafe impl Send for NamedPipeHandle {}

impl NamedPipeHandle {
    fn create(pipe_path: &str) -> io::Result<Self> {
        let pipe_path = to_wide_null(pipe_path);
        let handle = unsafe {
            CreateNamedPipeW(
                pipe_path.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                BUFFER_SIZE as u32,
                BUFFER_SIZE as u32,
                0,
                null(),
            )
        };

        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }

        Ok(Self(handle))
    }

    fn raw(&self) -> HANDLE {
        self.0
    }
}

impl Drop for NamedPipeHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct IncomingEnvelope {
    version: u32,
    message_type: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OutgoingEnvelope<'a> {
    version: u32,
    message_type: &'a str,
    payload: CommandReplyPayload<'a>,
}

#[derive(Serialize)]
struct CommandReplyPayload<'a> {
    status: &'a str,
    message: &'a str,
}
