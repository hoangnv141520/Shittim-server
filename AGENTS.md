# Repository Guidelines

## Project Structure & Module Organization

- `Shittim-Server/`: ASP.NET Core game server; protocol handlers live in `Core/NetworkProtocol/Handlers/`, with supporting `Services/`, `Controllers/`, and `Configuration/`.
- `Schale/`: shared game models, FlatBuffers definitions, Excel data schemas, and mapping profiles.
- `ShittimControlCenter/`: Electron launcher and administration UI; main-process modules sit at its root, UI code in `src/`, artwork in `Sprite/`, and Node tests in `test/`.
- `Shittim-Server.Tests/`: xUnit regression tests and `TestData/` fixtures.
- `Shittim-Coordinator/`, `MemoryDumper/`, `Tools/`, and `Scripts/`: supporting executables and tooling. `docs/` contains project documentation.

## Build, Test, and Development Commands

Use the .NET 10 SDK selected by `global.json` and Node.js 20, matching CI. Run these from the repository root:

- `dotnet build Shittim-Server/Shittim-Server.csproj -c Release`: build the server and project dependencies.
- `dotnet run --project Shittim-Server/Shittim-Server.csproj`: start the local server; initial startup downloads game resources.
- `dotnet test Shittim-Server.Tests/Shittim-Server.Tests.csproj -c Release --nologo`: run the .NET suite, including its Node test bridge.

From `ShittimControlCenter/`:

- `npm install`: install dependencies, including Electron; required before running tests or the UI.
- `npm test`: run the complete Node test suite.
- `npm run dev`: launch the development UI.
- `npm run dist`: build Windows distribution packages.

## Coding Style & Naming Conventions

Follow adjacent code: C# uses four-space indentation, PascalCase types and methods, and camelCase locals and parameters. JavaScript generally uses two spaces, single quotes, and camelCase functions. Keep filenames aligned with their classes or modules. Respect `.editorconfig` diagnostic settings; no dedicated formatter or lint script is configured.

## Testing Guidelines

Add focused regressions in `*Tests.cs` using xUnit and descriptive behavior-based method names; Node tests use `*.test.js`. No numeric coverage threshold is configured. Resource-dependent `[ExcelDumpFact]` tests skip without downloaded `Resources/Dumped` tables; report skips when validating changes.

## Commit & Pull Request Guidelines

History uses `fix:`, `feat:`, and `chore:` prefixes with concise descriptions. Keep commits focused. PRs should explain the problem, behavior change, and validation commands/results; link relevant issues and attach screenshots for UI changes. Release changes must synchronize `Directory.Build.props` and `ShittimControlCenter/package.json` versions.

## Security & Configuration

Keep local databases, certificates, credentials, request captures, and generated resources out of commits. Back up SQLite progress before testing migrations or update flows. Review configuration through `appsettings*.json` and `Configuration/` without committing local secrets.
