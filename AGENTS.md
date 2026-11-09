# Release delivery

- The user's permanent app path is `%LOCALAPPDATA%\Programs\MyCinema\MyCinema.App.exe`.
- To deliver an updated runnable version, run `powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1` from the repository. It runs the tests, publishes a self-contained Windows x64 app, updates that fixed installation, and creates Desktop and Start menu shortcuts.
- Use temporary build/artifact directories for development as needed, but direct the user to the installed app or its MyCinema shortcut for everyday use.
- The release script requires the installed app to be closed before replacement. Preserve the user's library, settings, posters, and history under `%LOCALAPPDATA%\MyCinema`.

<!-- postman-cli:installation:start -->
Postman CLI is installed on this device, and can be used for API Engineering work.
<!-- postman-cli:installation:end -->
