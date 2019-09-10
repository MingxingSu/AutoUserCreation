using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IrisUserAutoProcessor
{
	public static class StringHelper
	{
		#region Patterns
		public static readonly string PATTERN_HUB = @"(?<=:\s)\w{3}";//Sampe: ?: MAD
		public static readonly string PATTERN_ROLE = @"(?<=\?:\s).*(?=\()";//Sampe: ?: Read Only (Approval)
		public static readonly string PATTERN_USERNAME = @"(?<=\?:\s).+";//Sampe: ?: Mingxing SU
		public static readonly string PATTERN_USERID = @"\w+(?=@)";//Sampe: ?: Mingxing SU
		#endregion

		public static string GetFirstMatch(string input, string pattern) {
			Regex reg = new Regex(pattern);
			return reg.Match(input).Value;
		}
	}
}
