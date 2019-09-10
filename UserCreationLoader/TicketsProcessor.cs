using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
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
		private static readonly TextWriter _logger = File.CreateText(".//logs/" + DateTime.Now.ToString("yyyyMMddhhmmss") + ".txt");
		private static readonly string BaseURL = ConfigurationManager.AppSettings["ServiceNowHomePageUrl"];
		private Dictionary<string, string> UserNameIdDict = new Dictionary<string, string>();
		#endregion

		static TicketsProcessor()
		{
			InitIriHomePageUrls();

			InitRoleMappingDictionary();

			InitRoleConflictsDictionary();
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
			using (TextReader reader = File.OpenText("UserRequestTicketsList.txt"))
			{
				string taskListContent = reader.ReadToEnd();
				string[] lines = taskListContent.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
				foreach (string taskNO in lines)
				{
					bool done = ProcessSingleTicket(UserRequestPageUrl, taskNO);
				}
			}
		}

		public void TearDownBrowser()
		{
			driver.Quit();
		}


		#endregion

		#region Private Methods
		private bool ProcessSingleTicket(string ticketsPoolUrl, string ticketNumber)
		{
			try
			{
				//Open tickets pool page
				GoToUrlWithWait(ticketsPoolUrl, 5);

				//Open ticket page
				string ticketRelativeUrl = GetTicketUrl(ticketNumber);
				GoToUrlWithWait(BaseURL + ticketRelativeUrl, 3);

				string hub, roleName, searchUserUrl, userName;
				GetUserRequestDetails(out hub, out roleName, out searchUserUrl, out userName);

				string userID = GetUserID(searchUserUrl, userName);

				//Log user's details
				SnowTicket ticket = new SnowTicket(ticketNumber, hub, RoleMappingDict[roleName], userID, BaseURL + ticketRelativeUrl);

				LogTicketDetails(ticket);

				//Login to IRIS
				GoToIrisUserModule(ticket);

				//Search the user by userID in IRIS
				string userNameValue = LoadUserDetails(userID);

				bool _isSuccess = false;
				if (String.IsNullOrWhiteSpace(userNameValue))
					_isSuccess = CreateNewUserInIRIS(ticket);
				else _isSuccess = UpdateUser(ticket);

				_logger.Flush();

				//Close SNOW case
				if (_isSuccess) CompleteUserRequestTask(ticket);

				return _isSuccess;
			}
			catch (Exception ex)
			{
				_logger.WriteLine(ex.InnerException);
				_logger.Flush();
				return false;
			}
		}

		private string GetUserID(string searchUserUrl, string userName)
		{
			string userID = string.Empty;
			if (!UserNameIdDict.ContainsKey(userName))
			{
				//Search the user
				GoToUrlWithWait(searchUserUrl, 3);

				userID = GetUserIdBySearchingInSnow();

				UserNameIdDict.Add(userName, userID);
			}
			else userID = UserNameIdDict[userName];
			return userID;
		}

		private string LoadUserDetails(string userID)
		{
			IWebElement userIDSearchBox = driver.FindElement(By.Id("Username-searchbar-7065"));
			userIDSearchBox.SendKeys(userID);
			driver.FindElement(By.Id("searchbar-btn-search-7064")).Click();
			System.Threading.Thread.Sleep(2000);

			//Check user exists or not
			IWebElement userNameReadOnly = driver.FindElement(By.Id("Username-7065"));
			System.Threading.Thread.Sleep(2000);
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

		private string GetTicketUrl(string ticketNum)
		{
			//Switch iframe
			IWebElement gsftMainFrame = driver.FindElement(By.Id("gsft_main"));
			driver.SwitchTo().Frame(gsftMainFrame);

			//Get the Url of ticket
			IWebElement taskLink = driver.FindElement(By.XPath(String.Format("//a[contains(.,'{0}')]",
				ticketNum)));

			return taskLink != null ?
				taskLink.GetAttribute("ng-href")
				: string.Empty; ;
		}

		private string GetUserIdBySearchingInSnow()
		{

			//Find the found user and go to the user details page
			IWebElement useriFrame = driver.FindElement(By.TagName("iframe"));
			driver.SwitchTo().Frame(useriFrame);

			var links = driver.FindElements(By.TagName("a"));
			IWebElement userlink = driver.FindElement(By.XPath(@"//tr[contains(@class, 'list_row list_odd')]/td[3]/a"));
			userlink.Click();

			//Get user ID
			System.Threading.Thread.Sleep(2000);
			IWebElement email = driver.FindElement(By.Id("sys_readonly.sys_user.user_name"));
			string emailAdress = email.GetProperty("value");
			string userID = StringHelper.GetFirstMatch(emailAdress, StringHelper.PATTERN_USERID);
			return userID;
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
			System.Threading.Thread.Sleep(3000);

			//Go to security -> user module
			IWebElement sideBarSecurityManagerment = driver.FindElement(By.Id("sidebar-bp-SecurityManagement"));
			sideBarSecurityManagerment.Click();
			IWebElement sideBarUsers = driver.FindElement(By.Id("sidebar-wa-SECURITYUSERSAPP"));
			sideBarUsers.Click();
		}

		private static void LogTicketDetails(SnowTicket ticket)
		{
			_logger.WriteLine("--------------------------------------------");
			_logger.WriteLine(ticket.TicketNumber);
			_logger.WriteLine(ticket.TicketUrl);
			_logger.WriteLine(String.Join(",", ticket.Hub, ticket.RoleName, ticket.UserId));
			_logger.Flush();
		}

		private void GoToUrlWithWait(string url, int seconds)
		{
			driver.Navigate().GoToUrl(url);
			System.Threading.Thread.Sleep(seconds * 1000);
		}

		private bool UpdateUser(SnowTicket ticket)
		{
			SelectElement statusSelect = new SelectElement(driver.FindElement(By.XPath("//select[@id='StatusID-7114']")));
			string status = statusSelect.SelectedOption.GetAttribute("value");

			bool isSuccess = false;
			//Inactive
			if (status == ConfigurationManager.AppSettings["UserStatusValueInactive"])
			{
				isSuccess = ActivateUser(ticket, false);
			}
			//Locked temply
			else if (status == ConfigurationManager.AppSettings["UserStatusValueLocked"])
			{
				isSuccess = ActivateUser(ticket, true);
			}

			//Active User
			isSuccess = VerifyAndAssignRoleToUser(ticket, true);

			return isSuccess;
		}

		private bool ActivateUser(SnowTicket ticket, bool isLockedTemply)
		{
			try
			{
				IWebElement userDescTextBox = driver.FindElement(By.Id("Description-7065"));
				userDescTextBox.SendKeys(ticket.TicketNumber);

				IWebElement selectControl = driver.FindElement(By.CssSelector("#inner-content-wrapper > azur-webapp > azur-split-panel > div > azur-split-panel-section:nth-child(2) > div > azur-webapp-tabs > div > azur-users-tab > azur-one-to-one > div > div > azur-default-form > azur-form > div > div > div > form > azur-form-row:nth-child(2) > div > azur-form-column:nth-child(1) > div > azur-form-attribute:nth-child(3) > div > div > div > div > azur-form-field > div > azur-ref-field > div > div > div > azur-select-field > div > div"));
				selectControl.Click();
				System.Threading.Thread.Sleep(1000);

				Actions action = new Actions(driver);
				action.SendKeys(Keys.ArrowUp).Build().Perform();
				//Move 2 steps up to select ACTIVE status
				if (isLockedTemply)
				{
					action.SendKeys(Keys.ArrowUp).Build().Perform();
				}
				action.SendKeys(Keys.Enter).Build().Perform();
				System.Threading.Thread.Sleep(1000);

				IWebElement saveUserButton = driver.FindElement(By.Id("btn-header-Save-7065"));
				saveUserButton.Click();

				System.Threading.Thread.Sleep(2000);

				//TODO:Add log
				_logger.WriteLine(ticket.UserId + " : user status is updated to ACTIVE.");
				_logger.Flush();

				return true;
			}
			catch (Exception ex)
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
				System.Threading.Thread.Sleep(2000);

				IWebElement userIdTextBox = driver.FindElement(By.Id("Username-7065"));
				System.Threading.Thread.Sleep(1000);
				userIdTextBox.SendKeys(ticket.UserId);

				IWebElement userEmailTextBox = driver.FindElement(By.Id("Email-7065"));
				System.Threading.Thread.Sleep(1000);
				userEmailTextBox.SendKeys(ticket.UserId + "@iata.org");

				IWebElement userDescTextBox = driver.FindElement(By.Id("Description-7065"));
				System.Threading.Thread.Sleep(1000);
				userDescTextBox.SendKeys(" " + ticket.TicketNumber);

				IWebElement saveUserButton = driver.FindElement(By.Id("btn-header-Save-7065"));
				saveUserButton.Click();

				_logger.WriteLine("User has been ADDED in IRIS.");
				System.Threading.Thread.Sleep(5000);

				//Assign role to new user
				VerifyAndAssignRoleToUser(ticket, false);

				return true;
			}
			catch (Exception ex)
			{
				return false;
			}
		}

		private bool VerifyAndAssignRoleToUser(SnowTicket ticket, bool needsVerifyRoleExists)
		{
			//Check if role exists already, if so return directly
			driver.FindElement(By.CssSelector("#nav-tab-SecurityUsers-UserGroups > div > a")).Click();
			System.Threading.Thread.Sleep(3000);

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
					_logger.WriteLine("Role was assigned before");
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
					_logger.WriteLine(roleToAdd.Text + " : this role already exists.");

				IWebElement assignedList = driver.FindElement(By.Id("item-assigned-list"));

				builder.ClickAndHold(roleToAdd)
					.MoveToElement(assignedList)
				   .Release(assignedList)
				   .Build()
				   .Perform();
				System.Threading.Thread.Sleep(1000);

				driver.FindElement(By.Id("btn-header-Save-7117")).Click();
				System.Threading.Thread.Sleep(3000);

				//Error message pops up.
				if (driver.FindElement(By.Id("custom-modal-window")) != null)
				{
					_logger.WriteLine("WARNING: possbile role conflicts identfied, it is blocking role granting process,please maintain roles manually.");
				}
				_logger.WriteLine(ticket.RoleName + " : was added to user.");
			}
			catch (Exception ex)
			{
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
				System.Threading.Thread.Sleep(1000);

				driver.FindElement(By.Id("btn-header-Save-7117")).Click();
				System.Threading.Thread.Sleep(5000);

				//Error message pops up.
				if (driver.FindElement(By.Id("custom-modal-window")) != null)
				{
					_logger.WriteLine("WARNING: possbile error occured when removing role");
				}

				_logger.WriteLine(ticket.RoleName + " : role was removed.");
				_logger.Flush();

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
			System.Threading.Thread.Sleep(1000);
			assignedToTextBox.Click();
			assignedToTextBox.SendKeys(ConfigurationManager.AppSettings["AdminUserEmail"] + Keys.Tab);

			SelectElement caseStatusSelect = new SelectElement(driver.FindElement(By.Id("sc_task.state")));
			System.Threading.Thread.Sleep(1000);
			caseStatusSelect.SelectByText("Closed Complete");

			IWebElement saveButton = driver.FindElement(By.Id("sysverb_update_and_stay"));
			System.Threading.Thread.Sleep(1000);
			saveButton.Click();
			System.Threading.Thread.Sleep(3000);

			_logger.WriteLine(ticket.TicketNumber + " has been completed in SNOW.");
			_logger.Flush();
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
				if (_logger != null)
				{
					_logger.Flush();
					_logger.Dispose();
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
