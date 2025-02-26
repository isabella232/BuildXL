// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Threading;

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <nodoc />
    public static class DateTimeUtilities
    {
        private const string LastAccessedFormatString = "yyyyMMdd.HHmmss";

        /// <summary>
        /// Gets a readable string representation of a given <paramref name="time"/>.
        /// </summary>
        public static string ToReadableString(this DateTime time)
        {
            return time.ToString(LastAccessedFormatString, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Converts a given <paramref name="timeString"/> to <see cref="DateTime"/>.
        /// </summary>
        /// <returns>null if a given string is null or not valid.</returns>
        public static DateTime? FromReadableTimestamp(string timeString)
        {
            if (timeString == null)
            {
                return null;
            }

            if (DateTime.TryParseExact(timeString, LastAccessedFormatString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }

            return null;
        }

        /// <nodoc />
        public static bool IsRecent(this DateTime lastAccessTime, DateTime now, TimeSpan recencyInterval)
        {
            if (recencyInterval == Timeout.InfiniteTimeSpan)
            {
                return true;
            }

            return lastAccessTime + recencyInterval >= now;
        }

        public static bool IsStale(this DateTime lastAcccessTime, DateTime now, TimeSpan frequency)
        {
            return lastAcccessTime + frequency <= now;
        }

        /// <nodoc />
        public static TimeSpan Multiply(this TimeSpan timespan, double factor)
        {
            return TimeSpan.FromTicks((long)(timespan.Ticks * factor));
        }

        public static DateTime Max(this DateTime lhs, DateTime rhs)
        {
            if (lhs > rhs)
            {
                return lhs;
            }

            return rhs;
        }

        public static DateTime Min(this DateTime lhs, DateTime rhs)
        {
            if (lhs > rhs)
            {
                return rhs;
            }

            return lhs;
        }
    }
}
