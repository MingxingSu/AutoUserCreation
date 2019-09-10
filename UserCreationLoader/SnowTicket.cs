using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IrisUserAutoProcessor
{
	public class SnowTicket
	{
		public SnowTicket() {
		}

		public SnowTicket(string ticketNumber, string hub, string roleName, string userId,string ticketUrl)
		{
			TicketNumber = ticketNumber;
			Hub = hub;
			RoleName = roleName;
			UserId = userId;
			TicketUrl = ticketUrl;			
		}
		public string TicketNumber { get; }
		public string Hub { get; }
		public string RoleName { get; }
		public string UserId { get; }
		public string TicketUrl { get; }

	}
}
