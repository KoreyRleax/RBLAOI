using System;

namespace RBLAOI.Core.Utility
{
    public static class StringExtensions
    {
        public static double ToDouble(this string input, double defaultValue = 0)
        {
            return double.TryParse(input, out var value) ? value : defaultValue;
        }

        public static int ToInt(this string input, int defaultValue = 0)
        {
            return int.TryParse(input, out var value) ? value : defaultValue;
        }

        public static string ToStringOrDefault(this string input, string defaultValue = "")
        {
            return string.IsNullOrWhiteSpace(input) ? defaultValue : input;
        }
    }
}