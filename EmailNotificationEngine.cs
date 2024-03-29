﻿using System;
using System.Collections.Generic;
using System.Linq;
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
using Countersoft.Gemini.Extensibility.Apps;
using Countersoft.Gemini;
using Countersoft.Gemini.Commons.Dto.System;
using Countersoft.Gemini.Commons.Entity.Security;
using Unity;

namespace EmailAlerts
{
    [AppType(AppTypeEnum.Timer), 
    AppGuid("840D6D86-C74A-43D2-8D3A-3CF9985DB1D4"),
    AppName("Email Notification Engine"), 
    AppDescription("Sends email notifications to users")]
    public class EmailNotificationEngine : TimerJob
    {
        private List<AlertTemplate> _templates;

        private List<IssueTypeDto> _types;

        private List<PermissionSetDto> _permissionSets;

        private List<Organization> _organizations;

        private IssueManager _issueManager;

        private List<LanguageDto> _languages;

        public override bool Run(IssueManager issueManager)
        {
            if (!issueManager.UserContext.Config.EmailAlertsEnabled) return true;

            var tokenStore = issueManager.UserContext.Config.SmtpTokenStore;

            issueManager.UserContext.User.Entity = new User(issueManager.UserContext.User.Entity);

            issueManager.UserContext.User.Entity.Language = "en-US";

            _issueManager = issueManager;

            _templates = GeminiApp.Container.Resolve<IAlertTemplates>().FindWhere(c => c.AlertType != AlertTemplateType.Breeze).ToList();

            _types = new MetaManager(issueManager).TypeGetAll();

            _permissionSets = new PermissionSetManager(issueManager).GetAll();

            _organizations = new OrganizationManager(issueManager).GetAll();

            _languages = new LanguageManager(issueManager).GetActive();

            ProcessAppNavCardAlerts();

            ProcessWatcherAlerts();

            if (tokenStore != null && tokenStore.Length != 0 && !Enumerable.SequenceEqual(tokenStore, issueManager.UserContext.Config.SmtpTokenStore))
            {
                IConfiguration configuration = GeminiApp.Container.Resolve<IConfiguration>();
                configuration.SaveOAuthToken(issueManager.UserContext.Config.SmtpTokenStore);
            }
            
            return true;
        }

        private List<UserDto> GetCardSubscribers(NavigationCard card, NavigationCardsManager cardManager, UserManager userManager, UserDto owner)
        {
            if (!owner.Entity.Active) return new List<UserDto>();

            Dictionary<int, UserDto> subscribers = new Dictionary<int, UserDto>();

            subscribers.Add(owner.Entity.Id, owner);

            foreach (var user in card.CardData.Subscription.Users)
            {
                var userDto = userManager.Get(user);

                if(user != owner.Entity.Id && userDto != null && userDto.Entity.Active) subscribers.Add(user, userDto);
            }

            var groupUsers = cardManager.GetUsersFromGroups(card, card.CardData.Subscription.Groups);

            foreach (var user in groupUsers)
            {
                if (!subscribers.ContainsKey(user.Entity.Id)) subscribers.Add(user.Entity.Id, user);
            }
            
            return new List<UserDto>(subscribers.Values);
        }

        private void ProcessAppNavCardAlerts()
        {
            var navigationCardsManager = new NavigationCardsManager(_issueManager);

            List<NavigationCard> cards = navigationCardsManager.GetPendingAlertCards();

            LogDebugMessage("Email templates loaded: " + _templates.Count);

            LogDebugMessage("Pending card alerts: " + cards.Count);

            // ? We need to store user id and issue id for every email we send out -- avoids dupes?
            List<string> issuesEmailedToUsers = new List<string>(50);

            List<string> individualIssuesEmailedToUsers = new List<string>(50);

            AlertsTemplateHelper alerts = new AlertsTemplateHelper(_templates, GetUrl(_issueManager), _languages);

            UserManager userManager = new UserManager(_issueManager);

            bool refreshCache = false;

            var allOptOuts = navigationCardsManager.GetOptOuts();

            foreach (NavigationCard card in cards)
            {
                List<IssueDto> individualIssues = new List<IssueDto>();

                // Safety checks
                if (!card.UserId.HasValue) continue;

                if (card.CardData.Alerts.Count == 0) continue;

                UserDto recepient = userManager.Get(card.UserId.Value);

                // Safety check
                if (!recepient.Entity.EmailMe || recepient.Entity.Email.IsEmpty()) continue;

                DateTime lastChecked = card.CardData.AlertsLastSent.HasValue ? card.CardData.AlertsLastSent.Value : card.Created;

                DateTime lastCheckedLocal = lastChecked.ToLocal(_issueManager.UserContext.User.TimeZone);

                AlertTypeAppNavAlertsTemplateModel model = new AlertTypeAppNavAlertsTemplateModel();

                model.TheRecipient = recepient;

                model.Version = GeminiVersion.Version;

                model.GeminiUrl = alerts.GeminiUrl;

                List<int> issuesToAlert = new List<int>(card.CardData.Alerts);

                foreach (int issueId in issuesToAlert)
                {
                    IssueDto issue = null;
                    try
                    {
                        issue = _issueManager.Get(issueId);
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }
                    
                    // Safety check
                    if (issue == null || issue.Entity.IsNew) continue;

                    // Dupe check
                    string dupeKey = string.Format("{0}-{1}-{2}", recepient.Entity.Id, issueId, card.Id);

                    if (issuesEmailedToUsers.Contains(dupeKey)) continue;

                    var permissionManager = new PermissionsManager(recepient, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                    if (!permissionManager.CanSeeItem(issue.Project, issue)) continue;

                    foreach (var comment in issue.Comments)
                    {
                        if (!permissionManager.CanSeeComment(issue, comment))
                        {
                            issue.Comments.RemoveAll(c => !permissionManager.CanSeeComment(issue, c));
                            break;
                        }
                    }

                    // Remove the reported by first entry!
                    if(issue.History.Count > 0 && issue.History[issue.History.Count-1].Entity.AttributeChanged == ItemAttributeVisibility.ReportedBy) issue.History.RemoveAt(issue.History.Count -1); 
                    issue.ChangeLog = _issueManager.GetChangeLog(issue, _issueManager.UserContext.User, recepient, lastCheckedLocal);

                    // Populate model for email template
                    if (card.CardData.Subscription.IndividualAlert) individualIssues.Add(issue);

                    if (card.CardData.Subscription.Created && issue.Created.ToUtc(_issueManager.UserContext.User.TimeZone) >= lastChecked)
                    {
                        model.TheItemsCreated.Add(issue);
                    }
                    else
                    {
                        List<IssueAuditDto> allChanges = issue.History.FindAll(h => h.Entity.Created.ToUtc(_issueManager.UserContext.User.TimeZone) >= lastChecked);

                        List<IssueAuditDto> commentChanges = allChanges.FindAll(a => !a.Entity.IsCustom && a.Entity.AttributeChanged == ItemAttributeVisibility.AssociatedComments);

                        List<IssueAuditDto> nonCommentChanges = allChanges.FindAll(a => a.Entity.IsCustom || a.Entity.AttributeChanged != ItemAttributeVisibility.AssociatedComments);

                        // Add comments and updates
                        if (card.CardData.Subscription.Updated && nonCommentChanges.Count > 0 || card.CardData.Subscription.Commented && commentChanges.Count > 0 && issue.Comments.Count > 0)
                        {
                            model.TheItemsUpdated.Add(issue);
                        }

                        if (card.CardData.Subscription.Commented && commentChanges.Count > 0 && issue.Comments.Count > 0)
                        {
                            model.TheItemsCommented.Add(issue);
                        }
                    }

                    // Record the fact that we have processed this issue for this recepient (to prevent dupes)
                    issuesEmailedToUsers.Add(dupeKey);
                }

                model.CardTitle = string.Format("{0} {1}", card.Key, card.Title);

                model.CardKey = card.Key;

                model.CardDescription = card.Title;

                model.CardComment = card.CardData.Comment;

                model.CardUrl = string.Concat(model.GeminiUrl, "workspace/", card.Id, '/', card.Url);

                // Safety check!
                if (model.ChangeCount > 0)
                {
                    List<UserDto> subscribers = GetCardSubscribers(card, navigationCardsManager, userManager, recepient);

                    //if (!subscribers.Contains(recepient) && subscribers.Find(u => u.Entity.Id == recepient.Entity.Id) == null) subscribers.Insert(0, recepient);
                    if (card.CardData.Subscription.IndividualAlert)
                    {
                        foreach (var user in subscribers)
                        {
                            if (allOptOuts.Any(s => s.UserId == user.Entity.Id && s.CardId == card.Id && s.OptOutType == OptOutEmails.OptOutTypes.Alert)) continue;

                            foreach (var issue in individualIssues)
                            {
                                string individualDupeKey = string.Format("{0}-{1}", user.Entity.Id, issue.Entity.Id);

                                if (individualIssuesEmailedToUsers.Contains(individualDupeKey)) continue;

                                if (user != recepient)
                                {
                                    var permissionManager = new PermissionsManager(user, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                                    if (!permissionManager.CanSeeItem(issue.Project, issue)) continue;

                                    issue.ChangeLog = _issueManager.GetChangeLog(issue, _issueManager.UserContext.User, user, lastCheckedLocal);
                                }
                                
                                var indModel = new AlertTypeIndividualTemplateModel();

                                indModel.GeminiUrl = model.GeminiUrl;

                                indModel.LinkViewItem = NavigationHelper.GetIssueUrl(_issueManager.UserContext, issue.Entity.ProjectId, issue.EscapedProjectCode, issue.Entity.Id, null);

                                indModel.TheItem = issue;

                                indModel.TheRecipient = user;

                                indModel.Version = GeminiVersion.Version;

                                indModel.IsNewItem = model.TheItemsCreated.Contains(issue);

                                indModel.CardKey = model.CardKey;

                                indModel.CardDescription = model.CardDescription;

                                indModel.CardComment = model.CardComment;

                                indModel.CardUrl = model.CardUrl;

                                if (!indModel.IsNewItem && issue.ChangeLog.Count == 0) continue;

                                var template = alerts.FindTemplateForProject(
                                    indModel.IsNewItem ? AlertTemplateType.Created : AlertTemplateType.Updated, 
                                    issue.Entity.ProjectId, GetLanguageId(user) );

                                string html = alerts.GenerateHtml(template, indModel);

                                if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);
                                
                                // Send email
                                string log;

                                string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, indModel, true) : string.Format("[{0}] {1} {2} ({3})", issue.IssueKey, issue.Type, model.TheItemsCreated.Contains(issue) ? "Created" : "Updated", issue.Title, issue.IsClosed ? "Closed" : string.Empty);

                                EmailHelper.Send(_issueManager.UserContext.Config, subject, html, user.Entity.Email, user.Fullname, true, out log);

                                individualIssuesEmailedToUsers.Add(individualDupeKey);
                            }
                        }
                    }
                    else
                    {
                        var cloneCreated = new List<IssueDto>(model.TheItemsCreated);

                        var cloneUpdated = new List<IssueDto>(model.TheItemsUpdated);

                        var cloneCommented = new List<IssueDto>(model.TheItemsCommented);

                        // Find email template to use (for this project or fall back to default template)
                        
                        foreach (var user in subscribers)
                        {
                            AlertTemplate template = alerts.FindTemplateForProject( AlertTemplateType.AppNavAlerts, 0, GetLanguageId(user) );


                            if ( allOptOuts.Any(s => s.UserId == user.Entity.Id && s.CardId == card.Id && s.OptOutType == OptOutEmails.OptOutTypes.Alert)) continue;

                            model.TheItemsCreated = new List<IssueDto>(cloneCreated);

                            model.TheItemsUpdated = new List<IssueDto>(cloneUpdated);

                            model.TheItemsCommented = new List<IssueDto>(cloneCommented);

                            if (user != recepient)
                            {
                                var permissionManager = new PermissionsManager(user, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                                model.TheItemsCreated.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                model.TheItemsUpdated.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                model.TheItemsCommented.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                foreach (var issue in model.TheItemsCreated.Concat(model.TheItemsUpdated).Concat(model.TheItemsCommented))
                                {
                                    issue.ChangeLog = _issueManager.GetChangeLog(issue, _issueManager.UserContext.User, user, lastCheckedLocal);
                                }
                            }

                            //model.TheItemsCreated.RemoveAll(i => i.ChangeLog.Count == 0);
                            model.TheItemsUpdated.RemoveAll(i => i.ChangeLog.Count == 0);

                            model.TheItemsCommented.RemoveAll(i => i.ChangeLog.Count == 0);

                            if (model.ChangeCount == 0) continue;

                            // Generate email template
                            string html = alerts.GenerateHtml(template, model);

                            if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                            string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, model, true) : string.Format("{0} {1}", card.Key, card.Title);

                            // Send email
                            string log;

                            EmailHelper.Send(_issueManager.UserContext.Config, subject, html, user.Entity.Email, user.Fullname, true, out log);
                        }
                    }
                }

                // Remove the alert notifications and update the database
                lock (card.CardData.Alerts)
                {
                    card.CardData.Alerts.RemoveAll(a => issuesToAlert.Contains(a));
                }

                card.CardData.AlertsLastSent = DateTime.UtcNow;

                refreshCache = true;

                navigationCardsManager.Update(card, false, false);
            }

            if (refreshCache)
            {
                navigationCardsManager.Cache.NavigationCards.Invalidate();
                var webNodes = GeminiApp.Container.Resolve<IWebNodes>();
                webNodes.AddDataOnAllNodesButMe(new WebNodeData() { NodeGuid = GeminiApp.GUID, Key = "cache", Value = navigationCardsManager.Cache.NavigationCards.CacheKey });
            }
        }

        private void ProcessWatcherAlerts()
        {
            IConfiguration configuration = GeminiApp.Container.Resolve<IConfiguration>();
            var inputConfig = configuration.Get();
            SchedulerSettings settings = inputConfig.SchedulerSettings.HasValue() ? inputConfig.SchedulerSettings.FromJson<SchedulerSettings>() : new SchedulerSettings();

            DateTime lastChecked = settings.LastCheckedWatchers.HasValue ? settings.LastCheckedWatchers.Value : DateTime.UtcNow;

            IssuesFilter filter = new IssuesFilter();

            filter.RevisedAfter = lastChecked.ToString();

            filter.IncludeClosed = true;

            LogDebugMessage("Last checked for watched item alerts: " + lastChecked);

            settings.LastCheckedWatchers = DateTime.UtcNow;

            List<IssueDto> issues = _issueManager.GetFiltered(filter);

            LogDebugMessage("Item that have changed: " + issues.Count);
            
            if (issues.Count > 0) ProcessWatchers(issues, lastChecked);

            GeminiConfiguration config = configuration.Get();

            config.SchedulerSettings = settings.ToJson();

            ConfigurationItem item = new ConfigurationItem();

            item.SettingId = GeminiConfigurationOption.SchedulerSettings.ToString();

            item.SettingValue = config.SchedulerSettings;

            configuration.Update(item);

            GeminiApp.RefreshConfig(config);
        }

        private bool IsUserOnlyChange(List<IssueAuditDto> history, int userId)
        {
            return history.Find(h => h.Entity.UserId.GetValueOrDefault() != userId) == null;
        }

        private void ProcessWatchers(List<IssueDto> issues, DateTime lastChecked)
        {
            var lastCheckedLocal = lastChecked.ToLocal(_issueManager.UserContext.User.TimeZone);

            Dictionary<int, WatcherData> targets = new Dictionary<int, WatcherData>();
            Dictionary<string, WatcherData> emailTargets = new Dictionary<string, WatcherData>();
            var userManager = new UserManager(_issueManager);
            List<int> projectsMissingFollowerTemplate = new List<int>();
            int emailWatchers = -3;

            LogDebugMessage(string.Concat("Processing follower - ", issues.Count, " items found"));
            // Build array of users that are watching issues
            foreach (var issue in issues)
            {
                //Safety check
                if (issue.Watchers.Count == 0) continue;
                if (issue.Revised == issue.Created) continue;
                if (issue.Revised.ToUtc(_issueManager.UserContext.User.TimeZone) <= lastChecked) continue;

                var history = _issueManager.GetHistory(issue);

                issue.History = new List<IssueAuditDto>(history);

                history.RemoveAll(h => h.Entity.Created <= lastCheckedLocal);

                foreach (var watcher in issue.Watchers)
                {
                    if (watcher.Entity.UserId != null)
                    {
                        if (targets.ContainsKey(watcher.Entity.UserId.Value))
                        {
                            WatcherData data = targets[watcher.Entity.UserId.Value];

                            var permissionManager = new PermissionsManager(data.User, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                            if (!permissionManager.CanSeeItem(issue.Project, issue)) continue;

                            if (!data.User.Entity.EmailMeMyChanges && IsUserOnlyChange(history, data.User.Entity.Id)) continue;

                            data.IssueId.Add(issue.Entity.Id);
                        }
                        else
                        {
                            WatcherData data = new WatcherData();

                            data.User = userManager.Get(watcher.Entity.UserId.Value);

                            if (data.User.Entity.Active)
                            {
                                var permissionManager = new PermissionsManager(data.User, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                                if (!permissionManager.CanSeeItem(issue.Project, issue)) continue;

                                if (!data.User.Entity.EmailMeMyChanges && IsUserOnlyChange(history, data.User.Entity.Id)) continue;

                                data.IssueId.Add(issue.Entity.Id);

                                targets.Add(watcher.Entity.UserId.Value, data);
                            }
                        }
                    }
                    else
                    {
                        if (emailTargets.ContainsKey(watcher.Entity.Email.ToLower()))
                        {
                            WatcherData data = emailTargets[watcher.Entity.Email.ToLower()];
                            data = targets[data.User.Entity.Id];
                            data.IssueId.Add(issue.Entity.Id);
                        }
                        else
                        {
                            WatcherData data = new WatcherData();
                            data.User = new UserDto(new User());
                            data.User.Entity.Id = emailWatchers--;
                            data.User.Entity.Email = watcher.Entity.Email;
                            data.User.Entity.EmailMe = true;
                            data.User.Entity.EmailMeMyChanges = true;
                            data.User.Entity.ProjectGroups.Add(new ProjectGroupMembership() { ProjectGroupId = Constants.GlobalGroupEveryone, UserId = data.User.Entity.Id });
                            UserSettings settings = new UserSettings();
                            settings.IndividualFollowerAlerts = true;
                            data.User.Entity.Settings = settings.ToJson();
                            var group = new ProjectGroup() { Id = Constants.GlobalGroupEveryone, Members = new List<ProjectGroupMembership>() };
                            group.Members2.Add(new ProjectGroupMembership() { UserId = data.User.Entity.Id, ProjectGroupId = Constants.GlobalGroupEveryone });
                            data.User.ProjectGroups.Add(group);
                            data.IssueId.Add(issue.Entity.Id);
                            emailTargets.Add(watcher.Entity.Email.ToLower(), data);
                            targets.Add(data.User.Entity.Id, data);
                        }
                    }
                }
            }
            var langMan = new LanguageManager(_issueManager);
            AlertsTemplateHelper alerts = new AlertsTemplateHelper(_templates, GetUrl(_issueManager),_languages);

            // Now loop through users sending them watcher summary email
            Dictionary<int, List<IssueCommentDto>> originalComments = new Dictionary<int, List<IssueCommentDto>>();
            List<int> processedProjects;
            
            foreach (var target in targets)
            {
                processedProjects = new List<int>();

                if (originalComments.Count > 0)
                {
                    foreach (var kv in originalComments)
                    {
                        IssueDto issue = issues.Find(i => i.Entity.Id == kv.Key);

                        // Safety check
                        if (issue == null || issue.Entity.IsNew) continue;

                        issue.Comments = kv.Value;
                    }

                    originalComments = new Dictionary<int, List<IssueCommentDto>>();
                }

                var recipient = target.Value;

                // Safety check
                if (!recipient.User.Entity.EmailMe || recipient.User.Entity.Email.IsEmpty()) continue;

                AlertTypeWatchersTemplateModel model = new AlertTypeWatchersTemplateModel();

                model.TheRecipient = recipient.User;

                model.Version = GeminiVersion.Version;

                model.GeminiUrl = alerts.GeminiUrl;

                foreach (int issueId in recipient.IssueId)
                {
                    IssueDto issue = issues.Find(i => i.Entity.Id == issueId);

                    // Safety check
                    if (issue == null || issue.Entity.IsNew) continue;
                    
                    issue.ChangeLog = _issueManager.GetChangeLog(issue, _issueManager.UserContext.User, recipient.User, lastCheckedLocal);

                    var permissionManager = new PermissionsManager(recipient.User, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);

                    foreach (var comment in issue.Comments)
                    {
                        if (!permissionManager.CanSeeComment(issue, comment))
                        {
                            originalComments.Add(issueId, issue.Comments);

                            List<IssueCommentDto> comments = new List<IssueCommentDto>(issue.Comments);

                            comments.RemoveAll(c => !permissionManager.CanSeeComment(issue, c));

                            issue.Comments = comments;

                            break;
                        }
                    }

                    if (issue.ChangeLog.Count == 0) continue;

                    if (recipient.User.GetSettings().IndividualFollowerAlerts)
                    {
                        var template = alerts.FindTemplateForProject(AlertTemplateType.Updated, 
                            issue.Entity.ProjectId, GetLanguageId( recipient.User ) );

                        if (template == null)
                        {
                            LogDebugMessage("No update notification template found");
                            continue;
                        }

                        var indModel = new AlertTypeIndividualTemplateModel();

                        indModel.GeminiUrl = model.GeminiUrl;

                        indModel.LinkViewItem = NavigationHelper.GetIssueUrl(_issueManager.UserContext, issue.Entity.ProjectId, issue.EscapedProjectCode, issue.Entity.Id, null);

                        indModel.TheItem = issue;

                        indModel.TheRecipient = recipient.User;

                        indModel.Version = GeminiVersion.Version;

                        indModel.IsNewItem = false;

                        string html = alerts.GenerateHtml(template, indModel);

                        if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                        string log;

                        string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, indModel, true) : string.Format("[{0}] {1} {2} ({3})", issue.IssueKey, issue.Type, "Updated", issue.Title, issue.IsClosed ? "Closed" : string.Empty);
                        LogDebugMessage(string.Concat("Processing follower - Send item ", issue.IssueKey, " to ", recipient.User.Entity.Email));
                        EmailHelper.Send(_issueManager.UserContext.Config, subject, html, recipient.User.Entity.Email, recipient.User.Fullname, true, out log);
                    }
                    else
                    {
                        model.TheItemsUpdated.Add(issue);
                    }
                    
                }

                if (recipient.User.GetSettings().IndividualFollowerAlerts) continue;

                // Safety check!
                if (model.ChangeCount > 0)
                {
                    var watcherAlertTemplates = alerts.Templates.FindAll(s => s.AlertType == AlertTemplateType.Watchers);

                    if (watcherAlertTemplates.Count == 0)
                    {
                        LogDebugMessage("No follower notification template found");
                        continue;
                    }

                    if (!watcherAlertTemplates.Any(p => p.GetAssociatedProjectValue().IsEmpty()))
                    {
                        List<Project> allItemProjects = model.TheItemsUpdated.Select(s => s.Project).ToList();
                        allItemProjects = allItemProjects.Where(s => !watcherAlertTemplates.Any(a => a.GetAssociatedProjects().Contains(s.Id))).ToList();

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

                    foreach(var watcherTemplate in watcherAlertTemplates)
                    {
                        var allTemplateProjects = watcherTemplate.GetAssociatedProjects();

                        var issuesTemplate = allTemplateProjects.Count == 0 ? model.TheItemsUpdated : model.TheItemsUpdated.FindAll(s => allTemplateProjects.Contains(s.Entity.ProjectId));

                        if (issuesTemplate.Count == 0) continue;

                        var projectIds = issuesTemplate.Select(s => s.Entity.ProjectId).Distinct();

                        if (processedProjects.Count > 0)
                        {
                            projectIds = projectIds.Where(s => !processedProjects.Contains(s));
                            issuesTemplate = issuesTemplate.Where(s => !processedProjects.Contains(s.Entity.ProjectId)).ToList();
                        }

                        if (processedProjects.Contains(0) || projectIds.Count() == 0 || issuesTemplate.Count == 0) continue;

                        AlertTypeWatchersTemplateModel projectTemplateModel = new AlertTypeWatchersTemplateModel();

                        projectTemplateModel.TheItemsUpdated.AddRange(issuesTemplate);
                        projectTemplateModel.TheRecipient = model.TheRecipient;
                        projectTemplateModel.Version = model.Version;
                        projectTemplateModel.GeminiUrl = model.GeminiUrl;

                        AlertTemplate template = alerts.FindTemplateForProject(AlertTemplateType.Watchers, 
                            issuesTemplate.First().Entity.ProjectId, GetLanguageId(model.TheRecipient));

                        if (template.Id == 0)
                        {
                            
                            continue;
                        }

                        // Generate email template
                        string html = alerts.GenerateHtml(template, projectTemplateModel);

                        if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                        string subject = template.Options.Subject.HasValue() ? alerts.GenerateHtml(template, projectTemplateModel, true) : string.Format("{0} {1}", projectTemplateModel.ChangeCount, "Gemini Updates");

                        // Send email
                        string log;
                        LogDebugMessage(string.Concat("Processing follower - Send items ", issuesTemplate.Select(i=> i.IssueKey).ToDelimited(", "), " to ", recipient.User.Entity.Email));
                        EmailHelper.Send(_issueManager.UserContext.Config, subject, html, recipient.User.Entity.Email, recipient.User.Entity.Fullname, true, out log);

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

        private int? GetLanguageId( UserDto recipient )
        {
            var languageCode = recipient.Entity.Language.EnsureValue();
            var lang = _languages.Find( l => l.Code == languageCode )
                       ?? _languages.FindAll( l =>
                               l.Code.StartsWith( languageCode.Substring( 0, languageCode.IndexOf( "-" ) - 1 ) ) )
                           .FirstOrDefault();
            return lang == null ? null : new int?( lang.Entity.Id );
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
