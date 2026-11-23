using System;
using Autodesk.Revit.DB;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// GlobalId the Revit exporter gives the element: the export GUID (<see cref="ExportUtils.GetExportId"/>) compressed to
    /// 22 characters (IFC base 64). Links the Revit element to the entity in the exported IFC.
    /// </summary>
    public static class IfcGuid
    {
        private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        public static string For(Element element) => Compress(ExportUtils.GetExportId(element.Document, element.Id));

        public static string Compress(Guid guid)
        {
            byte[] b = guid.ToByteArray();
            ulong[] n =
            {
                b[3],
                b[2] * 65536UL + b[1] * 256UL + b[0],
                b[5] * 65536UL + b[4] * 256UL + b[7],
                b[6] * 65536UL + b[8] * 256UL + b[9],
                b[10] * 65536UL + b[11] * 256UL + b[12],
                b[13] * 65536UL + b[14] * 256UL + b[15],
            };
            var text = new char[22];
            int offset = 0;
            for (int i = 0; i < 6; i++)
            {
                int length = i == 0 ? 2 : 4;
                for (int j = 0; j < length; j++)
                {
                    text[offset + length - j - 1] = Chars[(int)(n[i] % 64)];
                    n[i] /= 64;
                }

                offset += length;
            }

            return new string(text);
        }
    }
}
