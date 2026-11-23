# BIMinFormation

Plugin do Revit que ajuda a preencher o modelo, desde a modelagem, com o que os arquivos `.ids`
([buildingSMART IDS 1.0](https://github.com/buildingSMART/IDS)) exigem do IFC.

*Revit add-in that helps fill the model, while modeling, with what buildingSMART IDS 1.0 files require from the IFC.
The interface is available in Brazilian Portuguese and English.*

## O que faz

- **Configurações**: escolhe a pasta e os arquivos `.ids` do projeto e mostra o que foi encontrado (fichas, esquema IFC,
  defeitos do arquivo).
- **Classificação**: indica, para cada elemento, a ficha do `.ids` que vale para ele (entidade IFC, tipo predefinido,
  identificação).
- **Preencher Parâmetros**: mostra o que cada ficha exige do elemento e grava os valores nos parâmetros do Revit,
  inclusive do edifício, do terreno e do projeto (Informações do projeto).
- **Pré-validação**: confere as fichas no modelo antes de exportar (passa, falha, a conferir) e, com **Conferir no IFC**,
  exporta um IFC temporário e avalia as fichas nele. Gera pendências em CSV, BCF e relatório.
- **Exportar IFC**: abre a exportação IFC do Revit com a configuração que as fichas pedem (versão IFC, quantidades,
  conjuntos de propriedades).

O plugin só lê os `.ids`; nunca os altera.

## Versões

- Revit 2023.1, 2025, 2026 e 2027.
- IFC2X3, IFC4 e IFC4X3_ADD2. O IFC 4.3 com quantidades sai do Revit 2026 em diante (Vista de Transferência de Projeto).

## Compilar a partir do código

1. Instale o [SDK .NET 10](https://dotnet.microsoft.com/download) (Windows).
2. Compile para o ano do Revit (`2023`, `2025`, `2026` ou `2027`):

   ```powershell
   dotnet build src\BIMinFormation.Revit\BIMinFormation.Revit.csproj -c Release -p:RevitVersion=2027
   ```

3. Com o Revit fechado, copie de `src\BIMinFormation.Revit\bin\R2027\Release\<framework>\` (`net48` no 2023,
   `net8.0-windows` no 2025 e 2026, `net10.0-windows` no 2027) para `%AppData%\Autodesk\Revit\Addins\2027\`:
   o `BIMinFormation.addin` na pasta do ano e as `.dll` numa subpasta `BIMinFormation\`.

## Licença

MIT, © 2026 Douglas W. Santos. Veja [LICENSE](LICENSE).
