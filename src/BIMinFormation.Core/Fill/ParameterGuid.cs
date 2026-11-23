using System;
using System.Security.Cryptography;
using System.Text;

namespace BIMinFormation.Core.Fill
{
    /// <summary>
    /// Deterministic GUID of the shared parameters the plugin creates (UUID version 5, RFC 4122): the same set, property,
    /// type and scope always give the same GUID, on any machine and even if the parameter file is recreated. Different
    /// sets never share a parameter (the door and window <c>OperationType</c> problem).
    /// </summary>
    public static class ParameterGuid
    {
        /// <summary>Fixed BIMinFormation namespace (do not change: it would change every GUID).</summary>
        public static readonly Guid Namespace = new Guid("6b0c1f9e-6e2b-4c1e-9c0a-3e6b2d7a9f41");

        public static Guid For(string propertySet, string property, string propertyType, bool typeScope)
        {
            string name = propertySet + "\u001f" + property + "\u001f" + propertyType + "\u001f" + (typeScope ? "T" : "I");
            return Create(Namespace, name);
        }

        public static Guid Create(Guid ns, string name)
        {
            byte[] nsBytes = ToNetworkOrder(ns.ToByteArray());
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] hash;
            using (var sha1 = SHA1.Create())
            {
                var input = new byte[nsBytes.Length + nameBytes.Length];
                Buffer.BlockCopy(nsBytes, 0, input, 0, nsBytes.Length);
                Buffer.BlockCopy(nameBytes, 0, input, nsBytes.Length, nameBytes.Length);
                hash = sha1.ComputeHash(input);
            }

            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
            return new Guid(ToNetworkOrder(bytes));
        }

        /// <summary>Guid.ToByteArray is little-endian in the first three fields; the RFC is big-endian.</summary>
        private static byte[] ToNetworkOrder(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            Array.Reverse(copy, 0, 4);
            Array.Reverse(copy, 4, 2);
            Array.Reverse(copy, 6, 2);
            return copy;
        }
    }
}
