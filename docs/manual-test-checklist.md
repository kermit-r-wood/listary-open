# Manual Windows Acceptance Checklist

- Start ListaryOpen.
- Open Notepad.
- Open Save As.
- Press Ctrl+G.
- Choose a known folder.
- Verify the Save As dialog changes to that folder.
- Start a non-elevated Win32 or .NET application with an Open dialog.
- Press Ctrl+G and verify folder jump.
- Start an administrator-elevated app with an Open dialog.
- Press Ctrl+G and verify ListaryOpen reports a permission-limited target.
- Open a custom unsupported dialog and verify ListaryOpen reports unsupported dialog.
