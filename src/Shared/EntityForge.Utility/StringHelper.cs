using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Provides string, stream and Base64 conversion utilities.</summary>
    public static class StringHelper
    {
        private const string NullDescription = "<null>";

        /// <summary>
        /// Base64url no-padding encodes the given input buffer.
        /// </summary>
        public static string Base64Encode(byte[] buffer, bool urlEncode = true, bool stripPadding = true)
        {
            string base64 = Convert.ToBase64String(buffer);

            // Converts base64 to base64url
            if (urlEncode)
            {
                base64 = base64.Replace("+", "-");
                base64 = base64.Replace("/", "_");
            }

            // Strips padding
            if (stripPadding)
            {
                base64 = base64.Replace("=", "");
            }

            return base64;
        }

        /// <summary>Encodes a nonnull string into a new memory stream, using UTF-8 when no encoding is supplied; the caller owns the stream.</summary>
        public static Stream ToStream(string content)
        {
            return ToStream(content, Encoding.UTF8);
        }

        /// <summary>Encodes a nonnull string into a new memory stream, using UTF-8 when no encoding is supplied; the caller owns the stream.</summary>
        public static Stream ToStream(string content, Encoding encoding)
        {
            ArgumentNullException.ThrowIfNull(content, nameof(content));
            byte[] bytes = encoding.GetBytes(content);

            return new MemoryStream(bytes);
        }

        /// <summary>
        /// Returns URI-safe data with a given input length.
        /// </summary>
        public static string GetRandomDataBase64Url(uint length)
        {
            RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create();
            byte[] bytes = new byte[length];
            randomNumberGenerator.GetBytes(bytes);
            return Base64Encode(bytes);
        }

        /// <summary>Returns the object's string representation, or the literal null marker when the object is null.</summary>
        public static string ToString(object obj)
        {
            if (obj == null)
            {
                return NullDescription;
            }

            return obj.ToString();
        }
    }
}
