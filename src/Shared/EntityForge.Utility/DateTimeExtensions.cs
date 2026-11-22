using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Converts between supported timestamp representations.</summary>
    public static class DateTimeExtensions
    {
        /// <summary>
        /// Converts the DateTime representation to a Unix timestamp, which is the number
        /// of non-leap seconds that have elapsed since 1st January 1970 00:00:00 UTC
        /// </summary>
        /// <param name="dateTime">The date and time, converted to UTC before calculating the timestamp.</param>
        /// <returns>The number of seconds since the Unix epoch.</returns>
        public static long ToUnixTime(this DateTime dateTime)
        {
            DateTimeOffset dto = new DateTimeOffset(dateTime.ToUniversalTime());
            return dto.ToUnixTimeSeconds();
        }

        /// <summary>Converts seconds since the Unix epoch to the system's local date and time.</summary>
        /// <param name="unixTimeStamp">The number of seconds since 1970-01-01T00:00:00Z.</param>
        /// <returns>A date and time with <see cref="DateTimeKind.Local"/> kind.</returns>
        public static DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dateTime;
        }
    }
}
