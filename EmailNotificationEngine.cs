using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Countersoft.Foundation.Commons.Extensions;
using Countersoft.Gemini.Commons;
using Countersoft.Gemini.Commons.Dto;
using Countersoft.Gemini.Commons.Entity;
using Countersoft.Gemini.Commons.Permissions;
using Countersoft.Gemini.Commons.System;
using Countersoft.Gemini.Contracts;
using Countersoft.Gemini.Contracts.Caching;
using Countersoft.Gemini.Contracts.Business;
using Countersoft.Gemini.Infrastructure.Helpers;
using Countersoft.Gemini.Infrastructure.Managers;
using Countersoft.Gemini.Infrastructure.TimerJobs;
using Countersoft.Gemini.Mailer;
using Microsoft.Practices.Unity;
using Countersoft.Gemini.Extensibility.Apps;
using Countersoft.Gemini;
using Countersoft.Gemini.Commons.Entity.Security;
using EmailNotificationEngine;

namespace EmailAlerts
{
    [AppType(AppTypeEnum.Timer), 
    AppGuid("840D6D86-C74A-43D2-8D3A-3CF9985DB1D4"),
    AppName("Email Notification Engine"), 
    AppDescription("Sends email notifications to users"),
    AppRequiresConfigScreen(true)]
    public class EmailNotificationEngine : TimerJob
    {
        private List<AlertTemplate> _templates;

        private List<IssueTypeDto> _types;

        private List<PermissionSetDto> _permissionSets;

        private List<Organization> _organizations;

        private IssueManager _issueManager;

        public override bool Run(IssueManager issueManager)
        {
            if (!issueManager.UserContext.Config.EmailAlertsEnabled) return true;


            issueManager.UserContext.User.Entity = new User(issueManager.UserContext.User.Entity);

            issueManager.UserContext.User.Entity.Language = "en-US";

            /*_issueManager = issueManager;
            */
            
            NotificationCache cache = new NotificationCache(issueManager, GetUrl(issueManager));

            IList<INotificationAlert> alerts = AlertFactory.GetAlerters(cache, issueManager);

            foreach (var alert in alerts)
            {
                LogDebugMessage("Running Process :" + alert.GetType().Name);
                alert.OnMessageRaised += OnMessageRaised;
                alert.CreateFileLog = true;
                alert.Send();
                LogDebugMessage(alert.Log.Aggregate((s1, s2) => s1 += ", \n\r" + s2));
            }

            //ProcessAppNavCardAlerts();

            //ProcessWatcherAlerts();
            
            return true;
        }

        private void OnMessageRaised(object sender, NotificationMessageEventArgs notificationMessageEventArgs)
        {
            LogDebugMessage(notificationMessageEventArgs.Message);
        }


        public override void Shutdown()
        {
            
        }

        public override TimerJobSchedule GetInterval(IGlobalConfigurationWidgetStore dataStore)
        {
            var data = dataStore.Get<TimerJobSchedule>(AppGuid);

            if (data == null || data.Value == null || (data.Value.Cron.IsEmpty() && data.Value.IntervalInHours.GetValueOrDefault() == 0 && data.Value.IntervalInMinutes.GetValueOrDefault() == 0))
            {
                int interval = GeminiApp.Config.EmailAlertsPollInterval.ToInt();

                return new TimerJobSchedule(interval == 0 ? 5 : interval);
            }

            return data.Value;
        }

    }

    public class WatcherData
    {
        public WatcherData()
        {
            IssueId = new List<int>();
        }

        public UserDto User { get; set; }
        public List<int> IssueId { get; set; }
    }
}
