using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;

namespace IrisUserAutoProcessor
{
	public class TicketsProcessor:IDisposable
	{
		#region Init Variables
		private IWebDriver driver;
		private static readonly Dictionary<string, string> AppDict = new Dictionary<string, string>(5);
		private static readonly Dictionary<string, string> RoleMappingDict = new Dictionary<string, string>();
		private static readonly Dictionary<string, string> RolesConflictsDict = new Dictionary<string, string>();
		private static readonly TextWriter Logger = File.CreateText(".//logs/" + DateTime.Now.ToString("yyyyMMddhhmmss") + ".txt");
		private static readonly string BaseURL = ConfigurationManager.AppSettings["ServiceNowHomePageUrl"];
		private static Dictionary<string, string> UserCacheDict = new Dictionary<string, string>();
		#endregion

		static TicketsProcessor()
		{
			InitIriHomePageUrls();

			InitRoleMappingDictionary();

			InitRoleConflictsDictionary();

			InitUserCacheDictionary();
		}

		#region Initiate Settings
		private static void InitIriHomePageUrls()
		{
			string serverUrlTemplate = ConfigurationManager.AppSettings["IrisLoginPageUrl"];
			string[] hubArr = new string[] { "AMM", "BJS", "MAD", "MIA", "SIN" };
			foreach (string hub in hubArr)
			{
				AppDict.Add(hub, String.Format(serverUrlTemplate, hub));
			}
		}

		private static void InitRoleMappingDictionary()
		{
			using (TextReader reader = File.OpenText("RoleNameMapping.txt"))
			{
				string fileContent = reader.ReadToEnd();
				string[] lines = fileContent.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
				for (int i = 1; i < lines.Length; i++)
				{
					string[] items = lines[i].Split(',');
					RoleMappingDict.Add(items[0], items[1]);
				}
			}
		}

		private static void InitRoleConflictsDictionary()
		{
			using (TextReader reader = File.OpenText("RolesConflictsMapping.txt"))
			{
				string fileContent = reader.ReadToEnd();
				string[] lines = fileContent.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
				for (int i = 1; i < lines.Length; i++)
				{
					string[] items = lines[i].Split(',');
					RolesConflictsDict.Add(items[0], items[1]);
				}
			}
		}

		private static void InitUserCacheDictionary()
		{
			var xml = XElement.Parse(File.ReadAllText("UserNamesIdCache.xml"));

			xml.Elements("user").ToList().ForEach(user => UserCacheDict.Add(user.Element("name").Value, user.Element("id").Value));
		}
		#endregion

		#region Public Methods
		public void SetUpBrowser()
		{
			ChromeOptions options = new ChromeOptions();
			options.AddArgument("--start-maximized");
			driver = new ChromeDriver(options);
		}
		
		public void ProcessIrisUserTickets(string UserRequestPageUrl)
		{
			try
			{
				while (true)
				{
					string ticketNumber = string.Empty;
					string ticketUrl = string.Empty;

					//Open tickets pool page
					GoToUrlWithWait(UserRequestPageUrl, 5);

					ticketUrl = FetchOneTicketUrl(out ticketNumber);

					if (string.IsNullOrWhiteSpace(ticketUrl) || string.IsNullOrWhiteSpace(ticketNumber))
						break;

					//Open ticket page
					GoToUrlWithWait(BaseURL + ticketUrl, 3);

					string hub, roleName, searchUserUrl, userName;
					GetUserRequestDetails(out hub, out roleName, out searchUserUrl, out userName);

					string userID = GetUserID(searchUserUrl, userName);

					if (!VerifyUserID(userID, userName))
					{
						WriteLog(string.Format("Found UserID '{0}' does NOT match with the given UserName '{1}'.", userID, userName));
					}
					//Log user's details
					SnowTicket ticket = new SnowTicket(ticketNumber, hub, RoleMappingDict[roleName], userID, BaseURL + ticketUrl);

					LogTicketDetails(ticket);

					//Login to IRIS
					GoToIrisUserModule(ticket);

					//Search the user by userID in IRIS
					string userNameValue = LoadUserDetails(userID);

					bool _isSuccess = false;
					if (String.IsNullOrWhiteSpace(userNameValue))
						_isSuccess = CreateNewUserInIRIS(ticket);
					else _isSuccess = UpdateUser(ticket);

					//Close SNOW case
					if (_isSuccess) CompleteUserRequestTask(ticket);
				}
			}
			catch (Exception ex)
			{
				WriteLog(ex.InnerException.Message);
				throw ex;
			}
			finally {
				//save user names and IDs in Dictionary to file on the disk.
				SaveUserCacheDictToXmlFile();				
			}
		}

		public void CloseBrowser()
		{
			driver.Quit();			
		}

		#endregion

		#region Private Methods
		private void SaveUserCacheDictToXmlFile()
		{
			XElement root = new XElement("root");
			foreach (var kv in UserCacheDict) {				
				root.Add(new XElement("user", new XElement("name", kv.Key), new XElement("id", kv.Value)));
			}
			using (XmlWriter xmlWriter = XmlWriter.Create("UserNamesIdCache.xml")) {
				root.WriteTo(xmlWriter);
			}			
		}

		private bool VerifyUserID(string userID, string userName)
		{
			return userName.TrimEnd().ToLowerInvariant().Split(' ').Any(u => userID.ToLowerInvariant().Contains(u));
		}

		private string GetUserID(string searchUserUrl, string userName)
		{
			//Check if user name has a ID in cache
			if (UserCacheDict.ContainsKey(userName))
				return UserCacheDict[userName];

			//Search the user
			GoToUrlWithWait(searchUserUrl, 3);

			string userID = GetUserIdBySearchingInSnow();

			//Save found userid to cache
			if (!UserCacheDict.ContainsKey(userID))
				UserCacheDict.Add(userName, userID);

			return userID;
		}

		private string LoadUserDetails(string userID)
		{
			IWebElement userIDSearchBox = driver.FindElement(By.Id("Username-searchbar-7065"));
			userIDSearchBox.SendKeys(userID);
			driver.FindElement(By.Id("searchbar-btn-search-7064")).Click();
			Sleep(2);

			//Check user exists or not
			IWebElement userNameReadOnly = driver.FindElement(By.Id("Username-7065"));
			Sleep(2);
			string userNameValue = userNameReadOnly.GetAttribute("value");
			return userNameValue;
		}

		private void GetUserRequestDetails(out string hub, out string roleName, out string URL_UserSearch, out string userName)
		{
			IWebElement descTextBox = driver.FindElement(By.Id("sc_task.description"));
			string description = descTextBox.Text;

			//Extract tickets details
			string[] detailedLinesOfDesc = description.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			hub = StringHelper.GetFirstMatch(detailedLinesOfDesc.First(s => s.Contains("Which Hub")), StringHelper.PATTERN_HUB).TrimEnd(' ');
			roleName = StringHelper.GetFirstMatch(detailedLinesOfDesc.First(s => s.Contains("type required")), StringHelper.PATTERN_ROLE).TrimEnd(' ');
			userName = StringHelper.GetFirstMatch(detailedLinesOfDesc.First(s => s.Contains("who needs access")), StringHelper.PATTERN_USERNAME).TrimEnd(' ');

			URL_UserSearch = String.Format(ConfigurationManager.AppSettings["ServiceNowSearchUserUrl"],
				 System.Web.HttpUtility.UrlEncode(userName));
		}

		private string FetchOneTicketUrl(out string ticketNumber)
		{
			//Switch iframe
			IWebElement gsftMainFrame = driver.FindElement(By.Id("gsft_main"));
			driver.SwitchTo().Frame(gsftMainFrame);

			string ticketUrl =string.Empty;
			ticketNumber = string.Empty;
			int rowIndex = 1;
			int ticketsToLookupMaxiumCount = int.Parse(ConfigurationManager.AppSettings["TicketsToLookupMaxiumCount"]);
			
			// get the first ticket of this page, if not found, try others.
			while (rowIndex <= ticketsToLookupMaxiumCount)
			{				
				IWebElement firstTicketEle = driver.FindElement(By.XPath(string.Format(@"//div[@id='task']/table[2]/tbody/tr[{0}]/td[3]/span/a", rowIndex ++))); 
				ticketNumber = firstTicketEle.Text.TrimStart().TrimEnd();

				if (ticketNumber.StartsWith("TASK"))
				{
					ticketUrl = firstTicketEle != null ? firstTicketEle.GetAttribute("ng-href") : string.Empty;
					break;
				}
			}
			return ticketUrl;

		}

		private string GetTicketUrl(string ticketNum)
		{
			//Switch iframe
			IWebElement gsftMainFrame = driver.FindElement(By.Id("gsft_main"));
			driver.SwitchTo().Frame(gsftMainFrame);

			//Get the Url of ticket
			IWebElement taskLink = driver.FindElement(By.XPath(String.Format("//a[contains(.,'{0}')]", ticketNum)));

			return taskLink != null ?  taskLink.GetAttribute("ng-href") : string.Empty;
		}

		private string GetUserIdBySearchingInSnow()
		{
			//Find the found user and go to the user details page
			IWebElement useriFrame = driver.FindElement(By.Id("gsft_main"));
			driver.SwitchTo().Frame(useriFrame);

			var userEmailEle = driver.FindElement(By.XPath(@"//section[@id='people-places_users']/section[1]/div/div[2]/address/ul/li[1]"));
			Sleep(4);

			return StringHelper.GetFirstMatch(userEmailEle.Text, StringHelper.PATTERN_USERID);
		}

		private void GoToIrisUserModule(SnowTicket ticket)
		{
			GoToUrlWithWait(AppDict[ticket.Hub], 3);

			IWebElement userNameInput = driver.FindElement(By.Id("txtUserName"));
			IWebElement passwordInput = driver.FindElement(By.Id("txtPassword"));
			userNameInput.SendKeys(ConfigurationManager.AppSettings["AdminUserName"]);
			passwordInput.SendKeys(ConfigurationManager.AppSettings["AdminUserPassword"]);

			IWebElement loginButton = driver.FindElement(By.Id("btnSubmit"));
			driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(120);
			loginButton.Click();
			Sleep(3);

			//Go to security -> user module
			IWebElement sideBarSecurityManagerment = driver.FindElement(By.Id("sidebar-bp-SecurityManagement"));
			sideBarSecurityManagerment.Click();
			IWebElement sideBarUsers = driver.FindElement(By.Id("sidebar-wa-SECURITYUSERSAPP"));
			sideBarUsers.Click();
		}

		private static void LogTicketDetails(SnowTicket ticket)
		{
			Logger.WriteLine("--------------------------------------------");
			Logger.WriteLine(ticket.TicketNumber);
			Logger.WriteLine(ticket.TicketUrl);
			Logger.WriteLine(String.Join(",", ticket.Hub, ticket.RoleName, ticket.UserId));
			Logger.Flush();
		}

		private void GoToUrlWithWait(string url, int seconds)
		{
			driver.Navigate().GoToUrl(url);
			Sleep(seconds);
		}

		private bool UpdateUser(SnowTicket ticket)
		{
			bool isSuccess = false;

			UserStatus userStatus = GetCurrentUserStatus();

			isSuccess = ActivateUser(ticket, userStatus);

			if (!isSuccess) return false;

			isSuccess = VerifyAndAssignRoleToUser(ticket, true);

			return isSuccess;
		}

		private UserStatus GetCurrentUserStatus()
		{
			SelectElement statusSelect = new SelectElement(driver.FindElement(By.XPath("//select[@id='StatusID-7114']")));
			string status = statusSelect.SelectedOption.GetAttribute("value");

			UserStatus userStatus;
			if (status == ConfigurationManager.AppSettings["UserStatusValueInactive"])
				userStatus = UserStatus.INACTIVE;
			else if (status == ConfigurationManager.AppSettings["UserStatusValueLocked"])
				userStatus = UserStatus.LOCKEDTEMPLY;
			else userStatus = UserStatus.ACTIVE;

			return userStatus;
		}

		private bool ActivateUser(SnowTicket ticket, UserStatus userStatus)
		{
			try
			{
				WriteLog(ticket.UserId + " : user status was " + Enum.GetName(typeof(UserStatus),userStatus));

				IWebElement userDescTextBox = driver.FindElement(By.Id("Description-7065"));
				userDescTextBox.SendKeys(ticket.TicketNumber);

				if (userStatus == UserStatus.INACTIVE)
				{
					IWebElement selectControl = driver.FindElement(By.CssSelector("#inner-content-wrapper > azur-webapp > azur-split-panel > div > azur-split-panel-section:nth-child(2) > div > azur-webapp-tabs > div > azur-users-tab > azur-one-to-one > div > div > azur-default-form > azur-form > div > div > div > form > azur-form-row:nth-child(2) > div > azur-form-column:nth-child(1) > div > azur-form-attribute:nth-child(3) > div > div > div > div > azur-form-field > div > azur-ref-field > div > div > div > azur-select-field > div > div"));
					selectControl.Click();
					Sleep(1);

					Actions action = new Actions(driver);
					action.SendKeys(Keys.ArrowUp).Build().Perform();

					//Move 2 steps up to select ACTIVE status
					if (userStatus == UserStatus.LOCKEDTEMPLY)
					{
						action.SendKeys(Keys.ArrowUp).Build().Perform();
					}
					action.SendKeys(Keys.Enter).Build().Perform();
					Sleep(1);

					WriteLog(ticket.UserId + " : user status is updated to ACTIVE.");
				}

				IWebElement saveUserButton = driver.FindElement(By.Id("btn-header-Save-7065"));
				saveUserButton.Click();

				Sleep(2);

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private bool CreateNewUserInIRIS(SnowTicket ticket)
		{
			try
			{
				IWebElement newUserButton = driver.FindElement(By.Id("btn-header-New-7065"));
				newUserButton.Click();
				Sleep(1);

				IWebElement userIdTextBox = driver.FindElement(By.Id("Username-7065"));
				Sleep(1);
				userIdTextBox.SendKeys(ticket.UserId);

				IWebElement userEmailTextBox = driver.FindElement(By.Id("Email-7065"));
				Sleep(1);
				userEmailTextBox.SendKeys(ticket.UserId + "@iata.org");

				IWebElement userDescTextBox = driver.FindElement(By.Id("Description-7065"));
				Sleep(1);
				userDescTextBox.SendKeys(">" + ticket.TicketNumber);

				IWebElement saveUserButton = driver.FindElement(By.Id("btn-header-Save-7065"));
				saveUserButton.Click();

				WriteLog("User has been ADDED in IRIS.");
				Sleep(3);

				int retryTimes = int.Parse(ConfigurationManager.AppSettings["DismissModalDialgoRetryTimes"]);
				DismissModalDiaglosIfExists(retryTimes);

				//Assign role to new user
				VerifyAndAssignRoleToUser(ticket, false);

				return true;
			}
			catch (Exception ex)
			{
				return false;
			}
		}

		private void DismissModalDiaglosIfExists(int retryCount)
		{
			while (retryCount-- > 0)
			{
				try
				{
					driver.SwitchTo().ActiveElement();

					driver.FindElement(By.XPath("//button[contains(@id,'btn-modal-Cancel-')]")).Click();

					Sleep(1);

				}
				catch (NoAlertPresentException) { }
				catch (Exception) { }
			}
		}

		private bool VerifyAndAssignRoleToUser(SnowTicket ticket, bool needsVerifyRoleExists)
		{
			//Check if role exists already, if so return directly
			driver.FindElement(By.CssSelector("#nav-tab-SecurityUsers-UserGroups > div > a")).Click();
			Sleep(3);

			if (needsVerifyRoleExists)
			{
				if (!IsRoleAssigned(ticket))
				{
					//User has No this role but exists exchange role, i.e: use request Assistant manager, but he already has mananger role.
					if (bool.Parse(ConfigurationManager.AppSettings["EnableResolveRoleConflicts"]))
					{
						var rolesConflicted = RolesConflictsDict[ticket.RoleName].Split('|');
						foreach (string roleToRemove in rolesConflicted)
						{
							SnowTicket exchangableRole = new SnowTicket("", "", roleToRemove, "", "");
							if (IsRoleAssigned(exchangableRole))
								RemoveRole(exchangableRole);
						}
					}
					return AssignNewRole(ticket);
				}
				else
				{
					//User has this role
					WriteLog("Role was assigned before");
					return true;
				}
			}
			//Assign role to new user
			return AssignNewRole(ticket);
		}

		private bool AssignNewRole(SnowTicket ticket)
		{
			try
			{
				Actions builder = new Actions(driver);

				var rolesList = driver.FindElements(By.XPath("//*[@id='item-unassigned-list']/div"));
				IWebElement roleToAdd = null;
				foreach (IWebElement role in rolesList)
				{
					IWebElement tempRoleControl = role.FindElement(By.TagName("span"));
					if (tempRoleControl.Text == ticket.RoleName)
					{
						roleToAdd = tempRoleControl;
						break;
					}
				}
				if (roleToAdd == null)
					WriteLog(roleToAdd.Text + " : this role already exists.");

				IWebElement assignedList = driver.FindElement(By.Id("item-assigned-list"));

				builder.ClickAndHold(roleToAdd)
					.MoveToElement(assignedList)
				   .Release(assignedList)
				   .Build()
				   .Perform();
				Sleep(1);

				driver.FindElement(By.Id("btn-header-Save-7117")).Click();
				Sleep(3);

				WriteLog(ticket.RoleName + " : has been added to user.");

			}
			catch (Exception ex)
			{
				return false;
			}

			return true;
		}

		private bool IsRoleAssigned(SnowTicket ticket)
		{
			var rolesAssignedList = driver.FindElements(By.XPath("//*[@id='item-assigned-list']/div"));
			IWebElement roleToVerify = null;
			foreach (IWebElement role in rolesAssignedList)
			{
				IWebElement tempAssignedRole = role.FindElement(By.TagName("span"));
				if (tempAssignedRole.Text == ticket.RoleName)
				{
					roleToVerify = tempAssignedRole;
					break;
				}
			}
			return roleToVerify != null;

		}

		private bool RemoveRole(SnowTicket ticket)
		{
			try
			{
				Actions builder = new Actions(driver);

				var rolesList = driver.FindElements(By.XPath("//*[@id='item-assigned-list']/div"));
				IWebElement roleToRemove = null;
				foreach (IWebElement role in rolesList)
				{
					IWebElement tempRoleControl = role.FindElement(By.TagName("span"));
					if (tempRoleControl.Text == ticket.RoleName)
					{
						roleToRemove = tempRoleControl;
						break;
					}
				}
				IWebElement unAssignedList = driver.FindElement(By.Id("item-unassigned-list"));

				builder.ClickAndHold(roleToRemove)
					.MoveToElement(unAssignedList)
				   .Release(unAssignedList)
				   .Build()
				   .Perform();
				Sleep(1);

				driver.FindElement(By.Id("btn-header-Save-7117")).Click();
				Sleep(10);//sometimes performance is not good

				WriteLog(ticket.RoleName + " : role has been removed due to conflicts with latest role.");

				//Error message pops up.
				if (driver.FindElement(By.Id("custom-modal-window")) != null)
				{
					WriteLog("WARNING: possbile error occured when removing role");
				}	

			}
			catch (Exception ex)
			{
			}

			return true;
		}

		private void CompleteUserRequestTask(SnowTicket ticket)
		{
			GoToUrlWithWait(ticket.TicketUrl, 2);

			IWebElement assignedToTextBox = driver.FindElement(By.Id("sys_display.sc_task.assigned_to"));
			Sleep(1);
			assignedToTextBox.Click();
			assignedToTextBox.SendKeys(ConfigurationManager.AppSettings["AdminUserEmail"] + Keys.Tab);

			SelectElement caseStatusSelect = new SelectElement(driver.FindElement(By.Id("sc_task.state")));
			Sleep(1);
			caseStatusSelect.SelectByText("Closed Complete");

			IWebElement saveButton = driver.FindElement(By.Id("sysverb_update_and_stay"));
			Sleep(1);
			saveButton.Click();
			Sleep(3);

			WriteLog(ticket.TicketNumber + " has been completed in SNOW.");			
		}

		private void WriteLog(string msg) {
			Logger.WriteLine(DateTime.Now.ToString("dd-MM-yyyy hh:mm:ss : ") + msg);
			Logger.Flush();
		}

		private void Sleep(int seconds) {
			Thread.Sleep(seconds * 1000);
		}
		#endregion

		#region IDisposable
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects).
				}

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.
				if (Logger != null)
				{
					Logger.Flush();
					Logger.Dispose();
				}
				if(driver !=null)
					driver.Quit();

				disposedValue = true;
			}
		}

		void IDisposable.Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion
	}
}
