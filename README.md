# BIMinFormation

**English** | [Português](README.pt-BR.md)

Revit add-in that helps fill the model, while modeling, with what `.ids` files
([buildingSMART IDS 1.0](https://github.com/buildingSMART/IDS)) require from the IFC. The interface is available in
English and Brazilian Portuguese.

## What it does

- **Settings**: picks the project's `.ids` folder and files and shows what was found (specifications, IFC schema,
  defects in the files).
- **Classification**: sets, for each element, the `.ids` specification that applies to it (IFC entity, predefined type,
  identification).
- **Fill In Parameters**: shows what each specification requires from the element and writes the values to Revit
  parameters, including the building, site and project (Project Information).
- **Pre-validation**: checks the specifications on the model before exporting (pass, fail, to check) and, with
  **Check in the IFC**, exports a temporary IFC and evaluates the specifications on it. Writes pending items as CSV, BCF
  and a report.
- **Export IFC**: opens the Revit IFC export with the configuration the specifications require (IFC version, quantities,
  property sets).

The add-in only reads the `.ids` files; it never changes them.

## Versions

- Revit 2023.1, 2025, 2026 and 2027.
- IFC2X3, IFC4 and IFC4X3_ADD2. IFC 4.3 with quantities requires Revit 2026 or newer (IFC4x3 Design Transfer View).

## Install

Download `BIMinFormation-<version>-setup.exe` from the [releases](https://github.com/dglsdws/BIMinFormation/releases)
page and run it with Revit closed. It installs for your user (no administrator rights) and asks for which Revit versions;
uninstall it from Settings > Apps. The first time Revit starts, choose "Always Load" when it asks about the unsigned
add-in. There is also a `.zip` to install by copying a folder (instructions inside).

## Build from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (Windows).
2. Build for the Revit year (`2023`, `2025`, `2026` or `2027`):

   ```powershell
   dotnet build src\BIMinFormation.Revit\BIMinFormation.Revit.csproj -c Release -p:RevitVersion=2027
   ```

3. With Revit closed, copy from `src\BIMinFormation.Revit\bin\R2027\Release\<framework>\` (`net48` for 2023,
   `net8.0-windows` for 2025 and 2026, `net10.0-windows` for 2027) to `%AppData%\Autodesk\Revit\Addins\2027\`:
   `BIMinFormation.addin` into the year folder and the `.dll` files into a `BIMinFormation\` subfolder.

## License

MIT, © 2026 Douglas W. Santos. See [LICENSE](LICENSE).
