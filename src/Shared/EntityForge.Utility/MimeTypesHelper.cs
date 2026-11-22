using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Maps recognized filename extensions to MIME content types.</summary>
    public static class MimeTypesHelper
    {
        /// <summary>The MIME content type for HTML documents.</summary>
        public const string TextHtmlMimeType = "text/html";
        /// <summary>The MIME content type for CSS stylesheets.</summary>
        public const string TextCssMimeType = "text/css";
        /// <summary>The MIME content type for unspecified binary data.</summary>
        public const string ApplicationOctetStreamMimeType = "application/octet-stream";

        private static readonly Dictionary<string, string> s_mimeTypeMappings = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
        {
            {".asf", "video/x-ms-asf"},
            {".asx", "video/x-ms-asf"},
            {".avi", "video/x-msvideo"},
            {".bin", ApplicationOctetStreamMimeType},
            {".cco", "application/x-cocoa"},
            {".crt", "application/x-x509-ca-cert"},
            {".css", TextCssMimeType},
            {".deb", ApplicationOctetStreamMimeType},
            {".der", "application/x-x509-ca-cert"},
            {".dll", ApplicationOctetStreamMimeType},
            {".dmg", ApplicationOctetStreamMimeType},
            {".ear", "application/java-archive"},
            {".eot", ApplicationOctetStreamMimeType},
            {".exe", ApplicationOctetStreamMimeType},
            {".flv", "video/x-flv"},
            {".gif", "image/gif"},
            {".hqx", "application/mac-binhex40"},
            {".htc", "text/x-component"},
            {".htm", TextHtmlMimeType},
            {".html", TextHtmlMimeType},
            {".ico", "image/x-icon"},
            {".img", ApplicationOctetStreamMimeType},
            {".iso", ApplicationOctetStreamMimeType},
            {".jar", "application/java-archive"},
            {".jardiff", "application/x-java-archive-diff"},
            {".jng", "image/x-jng"},
            {".jnlp", "application/x-java-jnlp-file"},
            {".jpeg", "image/jpeg"},
            {".jpg", "image/jpeg"},
            {".js", "application/x-javascript"},
            {".mml", "text/mathml"},
            {".mng", "video/x-mng"},
            {".mov", "video/quicktime"},
            {".mp3", "audio/mpeg"},
            {".mpeg", "video/mpeg"},
            {".mpg", "video/mpeg"},
            {".msi", ApplicationOctetStreamMimeType},
            {".msm", ApplicationOctetStreamMimeType},
            {".msp", ApplicationOctetStreamMimeType},
            {".pdb", "application/x-pilot"},
            {".pdf", "application/pdf"},
            {".pem", "application/x-x509-ca-cert"},
            {".pl", "application/x-perl"},
            {".pm", "application/x-perl"},
            {".png", "image/png"},
            {".prc", "application/x-pilot"},
            {".ra", "audio/x-realaudio"},
            {".rar", "application/x-rar-compressed"},
            {".rpm", "application/x-redhat-package-manager"},
            {".rss", "text/xml"},
            {".run", "application/x-makeself"},
            {".sea", "application/x-sea"},
            {".shtml", TextHtmlMimeType},
            {".sit", "application/x-stuffit"},
            {".swf", "application/x-shockwave-flash"},
            {".tcl", "application/x-tcl"},
            {".tk", "application/x-tcl"},
            {".txt", "text/plain"},
            {".war", "application/java-archive"},
            {".wbmp", "image/vnd.wap.wbmp"},
            {".wmv", "video/x-ms-wmv"},
            {".xml", "text/xml"},
            {".xpi", "application/x-xpinstall"},
            {".zip", "application/zip"},
        };

        /// <summary>Resolves the MIME type associated with a file extension.</summary>
        public static string GetMimeTypeFromExtension(string fileExtension)
        {
            if (string.IsNullOrEmpty(fileExtension))
            {
                throw new ArgumentException(nameof(fileExtension) + " cannot be null or empty", nameof(fileExtension));
            }

            string mimeType = null;
            if (s_mimeTypeMappings.TryGetValue(!fileExtension.StartsWith(".") ? ("." + fileExtension) : fileExtension, out mimeType))
            {
                return mimeType;
            }

            return ApplicationOctetStreamMimeType;
        }
    }
}
