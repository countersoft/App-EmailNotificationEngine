using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Web;
using Countersoft.Foundation.Commons.Extensions;
using Countersoft.Gemini;
using Countersoft.Gemini.Commons;
using Countersoft.Gemini.Commons.Dto;
using Countersoft.Gemini.Commons.Entity;
using Countersoft.Gemini.Commons.Entity.Security;
using Countersoft.Gemini.Commons.Permissions;
using Countersoft.Gemini.Commons.System;
using Countersoft.Gemini.Contracts;
using Countersoft.Gemini.Infrastructure.Helpers;
using Countersoft.Gemini.Infrastructure.Managers;
using Countersoft.Gemini.Mailer;
using EmailAlerts;
using Microsoft.Practices.Unity;

namespace EmailNotificationEngine
{
    public class FollowerNotification : BaseNotification, INotificationAlert
    {
        public FollowerNotification(NotificationCache cache, IssueManager issueManager) : base(cache, issueManager)
        {
        }

        protected override void ProcessNotifications()
        {
            ProcessWatcherAlerts();
        }


        private void ProcessWatcherAlerts()
        {
            SchedulerSettings settings = IssueManager.UserContext.Config.SchedulerSettings.HasValue()
                ? IssueManager.UserContext.Config.SchedulerSettings.FromJson<SchedulerSettings>()
                : new SchedulerSettings();

            DateTime lastChecked = settings.LastCheckedWatchers ?? DateTime.UtcNow;

            IssuesFilter filter = new IssuesFilter
            {
                RevisedAfter = lastChecked.ToString(),
                IncludeClosed = true
            };

            settings.LastCheckedWatchers = DateTime.UtcNow;

            LogDebugMessage("Last checked for watched item alerts: " + lastChecked + " next check will check from " + settings.LastCheckedWatchers + " (the time now)");

            //NOTE - this filter only works on the date, so loads all changed today. - It skips then in processing later, but why not remove them, save the effort.?
            List<IssueDto> changedIssues = IssueManager.GetFiltered(filter);
            //issue.Revised.ToUtc(IssueManager.UserContext.User.TimeZone) <= lastChecked
            var changedAfterLastRun = changedIssues.Count(d => d.Revised.ToUtc(IssueManager.UserContext.User.TimeZone) >= lastChecked);

            LogDebugMessage($"Item that have changed: {changedIssues.Count} (today),  {changedAfterLastRun} since the last run");

            if (changedIssues.Count > 0)
            {
                try
                {
                    ProcessWatchers(changedIssues, lastChecked);
                }
                catch(Exception ex)
                {
                    LogDebugMessage($"There was an error while Processing Followers - {ex.ToString()}");
                    CloseFile();
                    throw;
                }
            }

            IConfiguration configuration = GeminiApp.Container.Resolve<IConfiguration>();

            GeminiConfiguration config = configuration.Get();

            config.SchedulerSettings = settings.ToJson();

            ConfigurationItem item = new ConfigurationItem
            {
                SettingId = GeminiConfigurationOption.SchedulerSettings.ToString(),
                SettingValue = config.SchedulerSettings
            };

            configuration.Update(item);
            LogDebugMessage($"Updating scheduler settings, new datetime {settings.LastCheckedWatchers.GetValueOrDefault()}");

            GeminiApp.RefreshConfig(config);
        }


        private void ProcessWatchers(List<IssueDto> changedIssues, DateTime lastChecked)
        {
            
            var lastCheckedLocal = lastChecked.ToLocal(IssueManager.UserContext.User.TimeZone);

            Dictionary<int, WatcherData> targets = new Dictionary<int, WatcherData>();
            Dictionary<string, WatcherData> emailTargets = new Dictionary<string, WatcherData>();
            var userManager = new UserManager(IssueManager);
            List<int> projectsMissingFollowerTemplate = new List<int>();
            int emailWatchers = -3;

            LogDebugMessage($"Processing follower: {changedIssues.Count} items found (" +
                             $"{changedIssues.Select(i => i.Entity.Id.ToString()).Aggregate((s, s1) => s += ", " + s1)})");

            //List<string> debugInfo = new List<string>();
            // Build array of users that are watching issues
            foreach (var issue in changedIssues)
            {
                LogDebugMessage("",2);
                LogDebugMessage($"Processing {IssueDetail(issue)}", 2);

                //Safety check
                if (issue.Watchers.Count == 0)
                {
                    LogDebugMessage(IssueDetail(issue) + " did not have any watchers, Skipping", 3);
                    continue;
                }
                if (issue.Revised == issue.Created)
                {
                    LogDebugMessage(IssueDetail(issue) + " has just been created, skipping",3);
                    continue;
                }
                if (issue.Revised.ToUtc(IssueManager.UserContext.User.TimeZone) <= lastChecked)
                {
                    LogDebugMessage(IssueDetail(issue) + $" was revised {issue.Revised.ToUtc(IssueManager.UserContext.User.TimeZone)} before last checked {lastChecked}, skipping",3);
                    continue;
                }

                var history = IssueManager.GetHistory(issue);

                issue.History = new List<IssueAuditDto>(history);

                history.RemoveAll(h => h.Entity.Created <= lastCheckedLocal);

                LogDebugMessage($"{issue.Entity.Id} has {history.Count} change(s) to be notified to {issue.Watchers.Count} watchers:", 3);
                LogDebugMessage(issue.Watchers.Select(s => s.Fullname ?? s.Username ?? s.Entity.Id.ToString()).Aggregate((s, s1) => s += ", " + s1), 4);

                foreach (var watcher in issue.Watchers)
                {
                    LogDebugMessage($"Processing watcher: {UserDetail(watcher)}", 3);
                    if (watcher.Entity.UserId != null)
                    {
                        if (targets.ContainsKey(watcher.Entity.UserId.Value))
                        {
                            WatcherData data = targets[watcher.Entity.UserId.Value];

                            var permissionManager = new PermissionsManager(data.User, Cache.Types, Cache.PermissionSets, Cache.Organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);
                            //var permissionManager = new PermissionsManager(data.User, _types, _permissionSets, _organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);

                            if (!permissionManager.CanSeeItem(issue.Project, issue))
                            {
                                LogDebugMessage($"watcher does not have permission to view item {IssueDetail(issue)}", 4);
                                continue;
                            }

                            if (!data.User.Entity.EmailMeMyChanges && IsUserOnlyChange(history, data.User.Entity.Id))
                            {
                                LogDebugMessage($"Watcher has opted not to receive their changes, and this issue was changed solely by them", 4);
                                continue;
                            }

                            data.IssueId.Add(issue.Entity.Id);
                            LogDebugMessage($"User's notifications include these entities: " + data.IssueId.Cast<string>().Aggregate((s1,s2)=> s1 + ", " + s2));
                        }
                        else
                        {
                            WatcherData data = new WatcherData();

                            data.User = userManager.Get(watcher.Entity.UserId.Value);

                            if (data.User.Entity.Active)
                            {
                                var permissionManager = new PermissionsManager(data.User, Cache.Types, 
                                    Cache.PermissionSets, Cache.Organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);
                                //var permissionManager = new PermissionsManager(data.User, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                                if (!permissionManager.CanSeeItem(issue.Project, issue))
                                {
                                    LogDebugMessage($"watcher does not have permission to view item {IssueDetail(issue)}",4);
                                    continue;
                                }

                                if (!data.User.Entity.EmailMeMyChanges && IsUserOnlyChange(history, data.User.Entity.Id))
                                {
                                    LogDebugMessage($"Watcher has opted not to receive their changes, and this issue was changed solely by them",4);
                                    continue;
                                }

                                data.IssueId.Add(issue.Entity.Id);

                                targets.Add(watcher.Entity.UserId.Value, data);
                            }
                            else
                            {
                                LogDebugMessage($"User {data.User.Fullname} is not active and therefore will not be sent a notification",4);
                            }
                        }
                    }
                    else
                    {
                        LogDebugMessage($"Email **Subscription** user {watcher.Entity.Email.ToLower()}",4);
                        if (emailTargets.ContainsKey(watcher.Entity.Email.ToLower()))
                        {
                            WatcherData data = emailTargets[watcher.Entity.Email.ToLower()];
                            data = targets[data.User.Entity.Id];
                            data.IssueId.Add(issue.Entity.Id);
                        }
                        else
                        {
                            WatcherData data = new WatcherData();
                            data.User = new UserDto(new User())
                            {
                                Entity =
                                {
                                    Id = emailWatchers--,
                                    Email = watcher.Entity.Email,
                                    EmailMe = true,
                                    EmailMeMyChanges = true
                                }
                            };
                            data.User.Entity.ProjectGroups.Add(new ProjectGroupMembership { ProjectGroupId = Constants.GlobalGroupEveryone, UserId = data.User.Entity.Id });
                            UserSettings settings = new UserSettings { IndividualFollowerAlerts = true };
                            data.User.Entity.Settings = settings.ToJson();
                            var group = new ProjectGroup { Id = Constants.GlobalGroupEveryone, Members = new List<ProjectGroupMembership>() };
                            group.Members2.Add(new ProjectGroupMembership { UserId = data.User.Entity.Id, ProjectGroupId = Constants.GlobalGroupEveryone });
                            data.User.ProjectGroups.Add(group);
                            data.IssueId.Add(issue.Entity.Id);
                            emailTargets.Add(watcher.Entity.Email.ToLower(), data);
                            targets.Add(data.User.Entity.Id, data);
                        }
                    }
                }
            }

            LogDebugMessage("", 2);
            LogDebugMessage($"-------------------------------------------------------------------------------",2);
            LogDebugMessage($"------- STEP2 - Now process these change notifications to {targets.Count} people ---------",2);
            LogDebugMessage($"-------------------------------------------------------------------------------",2);

            AlertsTemplateHelper alerts = new AlertsTemplateHelper(Cache.Templates, Cache.BaseUrl);
            //AlertsTemplateHelper alerts = new AlertsTemplateHelper(_templates, GetUrl(_issueManager));

            // Now loop through users sending them watcher summary email
            Dictionary<int, List<IssueCommentDto>> originalComments = new Dictionary<int, List<IssueCommentDto>>();
            List<int> processedProjects;

            foreach (var target in targets) //Users
            {
                LogDebugMessage($"Processing User {target.Key} {target.Value?.User.Entity?.Username}",2);
                processedProjects = new List<int>();

                if (originalComments.Count > 0)
                {
                    foreach (var kv in originalComments)
                    {
                        IssueDto issue = changedIssues.Find(i => i.Entity.Id == kv.Key);

                        // Safety check
                        if (issue == null || issue.Entity.IsNew) continue;

                        issue.Comments = kv.Value;
                    }

                    originalComments = new Dictionary<int, List<IssueCommentDto>>();
                }

                var recipient = target.Value;

                // Safety check
                if (!recipient.User.Entity.EmailMe || recipient.User.Entity.Email.IsEmpty())
                {
                    LogDebugMessage($"{recipient.User.Fullname ?? recipient.User.Entity.Email ?? recipient.User.Entity.Id.ToString()} does not want emails, or has no email address set, skipping.",3);
                    continue;
                }

                AlertTypeWatchersTemplateModel model = new AlertTypeWatchersTemplateModel();

                model.TheRecipient = recipient.User;

                model.Version = GeminiVersion.Version;

                model.GeminiUrl = alerts.GeminiUrl;

                foreach (int issueId in recipient.IssueId)
                {
                    IssueDto issue = changedIssues.Find(i => i.Entity.Id == issueId);

                    // Safety check
                    if (issue == null || issue.Entity.IsNew)
                    {
                        LogDebugMessage($"Issue {issueId} could not be found or is marked as new, skipping",3);
                        continue;
                    }

                    issue.ChangeLog = IssueManager.GetChangeLog(issue, IssueManager.UserContext.User, recipient.User, lastCheckedLocal);

                    //var permissionManager = new PermissionsManager(recipient.User, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);
                    var permissionManager = new PermissionsManager(recipient.User, Cache.Types, Cache.PermissionSets, Cache.Organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);

                    foreach (var comment in issue.Comments)
                    {
                        if (!permissionManager.CanSeeComment(issue, comment))
                        {
                            originalComments.Add(issueId, issue.Comments);

                            List<IssueCommentDto> comments = new List<IssueCommentDto>(issue.Comments);

                            comments.RemoveAll(c => !permissionManager.CanSeeComment(issue, c));

                            issue.Comments = comments;

                            break; //TODO this break is before all the comments added to original comments, so original comments are not complete.
                        }
                    }

                    if (issue.ChangeLog.Count == 0)
                    {
                        LogDebugMessage($"{IssueDetail(issue)} does not have any changes from the ChangeLog, skipping",3);
                        continue;
                    }

                    if (recipient.User.GetSettings().IndividualFollowerAlerts)
                    {
                        LogDebugMessage($"{UserDetail(recipient.User)} wants individual email alerts", 3);
                        var template = alerts.FindTemplateForProject(AlertTemplateType.Updated, issue.Entity.ProjectId);

                        if (template == null)
                        {
                            LogDebugMessage("No update notification template found, skipping",3);
                            continue;
                        }

                        var indModel = new AlertTypeIndividualTemplateModel
                        {
                            GeminiUrl = model.GeminiUrl,
                            LinkViewItem = NavigationHelper.GetIssueUrl(IssueManager.UserContext, issue.Entity.ProjectId, issue.EscapedProjectCode, issue.Entity.Id),
                            TheItem = issue,
                            TheRecipient = recipient.User,
                            Version = GeminiVersion.Version,
                            IsNewItem = false
                        };

                        string html = alerts.GenerateHtml(template, indModel);

                        if (GeminiApp.GeminiLicense.IsFree)
                        {
                            html = alerts.AddSignature(html);
                        }

                        string log;

                        string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, indModel, true) : string.Format("[{0}] {1} {2} ({3})", issue.IssueKey, issue.Type, "Updated", issue.Title, issue.IsClosed ? "Closed" : string.Empty);
                        LogDebugMessage($"Processing follower - Send item {issue.IssueKey} to {recipient.User.Entity.Email}",3);
                        //LogDebugMessage(string.Concat("Processing follower - Send item ", issue.IssueKey, " to ", recipient.User.Entity.Email));
                        EmailHelper.Send(IssueManager.UserContext.Config, subject, html, recipient.User.Entity.Email, recipient.User.Fullname, true, out log);
                        LogDebugMessage($"Sending email to {recipient.User.Entity.Email} ({recipient.User.Entity.Fullname}) - {subject}");
                    }
                    else
                    {
                        LogDebugMessage($"{UserDetail(recipient.User)} wants batched emails, added issue {issue.IssueKey} to updated item list", 3);
                        model.TheItemsUpdated.Add(issue);
                    }

                }

                if (recipient.User.GetSettings().IndividualFollowerAlerts)
                {
                    LogDebugMessage($"Individual Alerts for this user has finished, moving to next user",2);
                    continue;
                }

                // Safety check!
                if (model.ChangeCount > 0)
                {
                    var watcherAlertTemplates = alerts.Templates.FindAll(s => s.AlertType == AlertTemplateType.Watchers);

                    if (watcherAlertTemplates.Count == 0)
                    {
                        LogDebugMessage("No follower notification template found",1);
                        continue;
                    }

                    if (!watcherAlertTemplates.Any(p => p.GetAssociatedProjectValue().IsEmpty()))
                    {
                        List<Project> allItemProjects = model.TheItemsUpdated
                            .Select(item => item.Project)
                            .Where(project => !watcherAlertTemplates.Any(template => template.GetAssociatedProjects().Contains(project.Id)))
                            .ToList();

                        if (projectsMissingFollowerTemplate.Count > 0)
                        {
                            allItemProjects = allItemProjects.Where(s => !projectsMissingFollowerTemplate.Contains(s.Id)).ToList();
                        }

                        if (allItemProjects.Count > 0)
                        {
                            LogDebugMessage(string.Concat("No follower notification template found for project ", string.Join(", ", allItemProjects.Select(s => s.Name).Distinct())));
                            projectsMissingFollowerTemplate.AddRange(allItemProjects.Select(s => s.Id).Distinct());
                        }
                    }

                    watcherAlertTemplates.Sort((x, y) => y.GetAssociatedProjectValue().CompareTo(x.GetAssociatedProjectValue()));

                    foreach (var watcherTemplate in watcherAlertTemplates)
                    {
                        var allTemplateProjects = watcherTemplate.GetAssociatedProjects();

                        var issueForTemplate = allTemplateProjects.Count == 0 ? model.TheItemsUpdated : model.TheItemsUpdated.FindAll(s => allTemplateProjects.Contains(s.Entity.ProjectId));

                        if (issueForTemplate.Count == 0)
                        {
                            LogDebugMessage($"There are no issues for the selected watcher template - {watcherTemplate.Label}",2);
                            continue;
                        }


                        var projectIds = issueForTemplate.Select(s => s.Entity.ProjectId).Distinct();

                        if (processedProjects.Count > 0)
                        {
                            projectIds = projectIds.Where(s => !processedProjects.Contains(s));
                            issueForTemplate = issueForTemplate.Where(s => !processedProjects.Contains(s.Entity.ProjectId)).ToList();
                        }

                        if (processedProjects.Contains(0) || projectIds.Count() == 0 || issueForTemplate.Count == 0)
                        {
                            LogDebugMessage($" Processed the all projects, or no issues for the current template {processedProjects.Contains(0)}|{projectIds.Count()}|{issueForTemplate.Count}", 2);
                            continue;
                        }

                        AlertTypeWatchersTemplateModel projectTemplateModel = new AlertTypeWatchersTemplateModel();

                        projectTemplateModel.TheItemsUpdated.AddRange(issueForTemplate);
                        projectTemplateModel.TheRecipient = model.TheRecipient;
                        projectTemplateModel.Version = model.Version;
                        projectTemplateModel.GeminiUrl = model.GeminiUrl;

                        AlertTemplate template = alerts.FindTemplateForProject(AlertTemplateType.Watchers, issueForTemplate.First().Entity.ProjectId);

                        if (template.Id == 0)
                        {
                            LogDebugMessage($"Template id was zero",2);
                            continue;
                        }

                        // Generate email template
                        string html = alerts.GenerateHtml(template, projectTemplateModel);

                        if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                        string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, projectTemplateModel, true) : string.Format("{0} {1}", projectTemplateModel.ChangeCount, "Gemini Updates");

                        // Send email
                        string log;
                        LogDebugMessage(string.Concat("Processing follower - Send items ", issueForTemplate.Select(i => i.IssueKey).ToDelimited(", "), " to ", recipient.User.Entity.Email));
                        EmailHelper.Send(IssueManager.UserContext.Config, subject, html, recipient.User.Entity.Email, recipient.User.Entity.Fullname, true, out log);

                        LogDebugMessage($"Sending email to {recipient.User.Entity.Email} {recipient.User.Entity.Fullname} - {subject}",3);

                        if (allTemplateProjects.Count == 0)
                        {
                            processedProjects.Add(0);
                        }
                        else
                        {
                            processedProjects.AddRange(projectIds);
                        }
                    }
                }
            }
        }

        private string IssueDetail(IssueDto issue)
        {
            return $"Item {issue.Title} ({issue.ProjectCode}:{issue.Entity.Id})";
        }

        private string UserDetail(UserDto user)
        {
            if (user.Fullname.HasValue())
            {
                return $"User {user.Fullname} ";
            }
            if (user.Entity.Username.HasValue())
            {
                return $"User {user.Entity.Username} ";
            }
            if (user.Entity.Email.HasValue())
            {
                return $"User {user.Entity.Email} ";
            }
            if (user.Entity != null)
            {
                return $"User {user.Entity.Id}";
            }
            return "unknown user";
        }

        private string UserDetail(IssueWatcherDto user)
        {
            return UserDetail(new UserDto
            {
                Entity = new User { Email = user.Email, Username = user.Username, Id = user.Entity.Id },
                Fullname = user.Fullname
            });
        }

        private bool IsUserOnlyChange(List<IssueAuditDto> history, int userId)
        {
            return history.Find(h => h.Entity.UserId.GetValueOrDefault() != userId) == null;
        }
    }
}