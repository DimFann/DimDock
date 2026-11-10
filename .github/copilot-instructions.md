# DimDock repository rules

DimDock is an ASP.NET Core Razor Pages gallery and blog site (`website/`) with a
Python path loader (`path_loader/`), MariaDB, and Local or Linode Object
Storage, run with Docker Compose. The live site is https://dimdock.com.

## Main README.md

The main README exists to help someone **set up and run the project
themselves**. Keep it short and focused on that goal with every edit:

- Mention that the project is in development.
- Keep a table explaining the available `.env` values.
- Only cover the main website and its startup scripts. Content for other tools
  (e.g. `gdrive_scripts/`) belongs in a README inside that folder.
- Don't describe every feature; include only what's needed for setup,
  configuration, deployment and running tests.
- Keep the Server setup section's note that dimdock.com is an example of the
  live deployment.
- When adding something, prefer cutting or condensing existing text over growing
  the file.

## Code changes

- Make small, surgical changes; update directly related docs (README tables,
  `TODO_*.md`) when behavior changes.
- Run `dotnet test DimDock.sln` for C# changes. The other `tests\` projects are
  `dotnet run` check programs; see the README tests table for their
  requirements.
- Never read, print or commit secrets from `.env`.
