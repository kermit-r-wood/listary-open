# Project workflow

- After completing any repository change, always build the distributable Windows package before handing the work back to the user.
- Follow `.github/workflows/package.yml`: run the relevant tests, publish the Release `win-x64` app with native hooks, verify all required executables and hook files, and create `artifacts/ListaryOpen-win-x64.zip`.
- Report whether packaging succeeded and provide the final package path in the handoff.
