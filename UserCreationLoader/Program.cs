using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;


namespace IrisUserAutoProcessor
{
	class Program
	{
		static void Main(string[] args)
		{
			TicketsProcessor ticketsProcessor = new TicketsProcessor();

			ticketsProcessor.SetUpBrowser();

			ticketsProcessor.ProcessIrisUserTickets(ConfigurationManager.AppSettings["UserRequestPoolsUrl"]);			

			ticketsProcessor.TearDownBrowser();

			Console.WriteLine("All Done!");
			Console.ReadKey();
		}
	}


}
