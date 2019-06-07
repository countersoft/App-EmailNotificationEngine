using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Countersoft.Foundation.Commons.Extensions;
using Countersoft.Gemini;
using Countersoft.Gemini.Commons;
using Countersoft.Gemini.Commons.Dto;
using Countersoft.Gemini.Commons.Entity;
using Countersoft.Gemini.Commons.Permissions;
using Countersoft.Gemini.Contracts.Business;
using Countersoft.Gemini.Infrastructure.Helpers;
using Countersoft.Gemini.Infrastructure.Managers;
using Countersoft.Gemini.Mailer;
using Countersoft.Gemini.Models;
using Microsoft.Practices.Unity;

namespace EmailNotificationEngine
{
    public class WorkspaceNotification: BaseNotification, INotificationAlert
    {

        public WorkspaceNotification(NotificationCache cache, IssueManager issueManager) 
            : base(cache, issueManager)
        {

        }

        protected override void ProcessNotifications()
        {
            ProcessAppNavCardAlerts();
        }

        private void ProcessAppNavCardAlerts()
        {
            var navigationCardsManager = new NavigationCardsManager(IssueManager);

            List<NavigationCard> cards = navigationCardsManager.GetPendingAlertCards();

            //LogDebugMessage("Email templates loaded: " + _templates.Count);
            LogDebugMessage("Email templates loaded: " + Cache.Templates.Count);

            LogDebugMessage("Pending card alerts: " + cards.Count);

            // ? We need to store user id and issue id for every email we send out -- avoids dupes?
            List<string> issuesEmailedToUsers = new List<string>(50);

            List<string> individualIssuesEmailedToUsers = new List<string>(50);

            //AlertsTemplateHelper alerts = new AlertsTemplateHelper(_templates, GetUrl(_issueManager));
            AlertsTemplateHelper alerts = new AlertsTemplateHelper(Cache.Templates, Cache.BaseUrl);

            UserManager userManager = new UserManager(IssueManager);

            bool refreshCache = false;

            var allOptOuts = navigationCardsManager.GetOptOuts();

            foreach (NavigationCard card in cards)
            {
                List<IssueDto> individualIssues = new List<IssueDto>();

                // Safety checks
                if (!card.UserId.HasValue)
                {
                    LogDebugMessage($"Card {card.Id} is not assigned to a user!",2);
                    continue;
                }

                if (card.CardData.Alerts.Count == 0)
                {
                    LogDebugMessage($"Card {card.Id} is does not have any alerts!",2);
                    continue;
                }
                UserDto recepient = userManager.Get(card.UserId.Value);
                LogDebugMessage($"Processing Card {card.Key} ({card.Id}) for user {recepient.Entity.Username}, last sent {card.CardData.AlertsLastSent}",2);

                // Safety check
                if (!recepient.Entity.EmailMe || recepient.Entity.Email.IsEmpty())
                {
                    LogDebugMessage($"User does not want emails, or email address not supplied",2);
                    continue;
                }

                DateTime lastChecked = card.CardData.AlertsLastSent.HasValue ? card.CardData.AlertsLastSent.Value : card.Created;

                DateTime lastCheckedLocal = lastChecked.ToLocal(IssueManager.UserContext.User.TimeZone);

                AlertTypeAppNavAlertsTemplateModel model = new AlertTypeAppNavAlertsTemplateModel();

                model.TheRecipient = recepient;

                model.Version = GeminiVersion.Version;

                model.GeminiUrl = alerts.GeminiUrl;

                List<int> issuesToAlert = new List<int>(card.CardData.Alerts);
                LogDebugMessage($"Card has {issuesToAlert.Count} issues to alert", 2);

                foreach (int issueId in issuesToAlert)
                {
                    IssueDto issue = null;
                    try
                    {
                        issue = IssueManager.Get(issueId);
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                    }

                    // Safety check
                    if (issue == null || issue.Entity.IsNew)
                    {
                        LogDebugMessage($"Item is null or is new - {issue?.Entity.Title}",2);
                        continue;
                    }

                    // Dupe check
                    string dupeKey = string.Format("{0}-{1}-{2}", recepient.Entity.Id, issueId, card.Id);

                    if (issuesEmailedToUsers.Contains(dupeKey))
                    {
                        LogDebugMessage($"Already sent email to this user for this issue and card {dupeKey}");
                        continue;
                    }

                    //var permissionManager = new PermissionsManager(recepient, _types, _permissionSets, _organizations, _issueManager.UserContext.Config.HelpDeskModeGroup, false);
                    var permissionManager = new PermissionsManager(recepient, Cache.Types, Cache.PermissionSets, Cache.Organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);

                    if (!permissionManager.CanSeeItem(issue.Project, issue)) continue;


                    //TODO this is nonsense code... no need to loop if removing all items...

                    try
                    {
                        foreach (var comment in issue.Comments)
                        {
                            if (!permissionManager.CanSeeComment(issue, comment))
                            {
                                issue.Comments.RemoveAll(c => !permissionManager.CanSeeComment(issue, c));
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebugMessage($"There was an error with comments and permissions for item {issue.Title}");
                    }

                    // Remove the reported by first entry!
                    if (issue.History.Count > 0 && issue.History[issue.History.Count - 1].Entity.AttributeChanged == ItemAttributeVisibility.ReportedBy) issue.History.RemoveAt(issue.History.Count - 1);
                    issue.ChangeLog = IssueManager.GetChangeLog(issue, IssueManager.UserContext.User, recepient, lastCheckedLocal);

                    // Populate model for email template
                    if (card.CardData.Subscription.IndividualAlert) individualIssues.Add(issue);

                    if (card.CardData.Subscription.Created && issue.Created.ToUtc(IssueManager.UserContext.User.TimeZone) >= lastChecked)
                    {
                        LogDebugMessage($"Issue is new and created subscription is requested",2);
                        model.TheItemsCreated.Add(issue);
                    }
                    else
                    {
                        List<IssueAuditDto> allChanges = issue.History.FindAll(h => h.Entity.Created.ToUtc(IssueManager.UserContext.User.TimeZone) >= lastChecked);

                        List<IssueAuditDto> commentChanges = allChanges.FindAll(a => !a.Entity.IsCustom && a.Entity.AttributeChanged == ItemAttributeVisibility.AssociatedComments);

                        List<IssueAuditDto> nonCommentChanges = allChanges.FindAll(a => a.Entity.IsCustom || a.Entity.AttributeChanged != ItemAttributeVisibility.AssociatedComments);

                        // Add comments and updates
                        if (card.CardData.Subscription.Updated && nonCommentChanges.Count > 0 || card.CardData.Subscription.Commented && commentChanges.Count > 0 && issue.Comments.Count > 0)
                        {
                            LogDebugMessage($"Issue is updated and updated subscription was requested", 2);
                            model.TheItemsUpdated.Add(issue);
                        }

                        if (card.CardData.Subscription.Commented && commentChanges.Count > 0 && issue.Comments.Count > 0)
                        {
                            LogDebugMessage($"Issue has been commented on and comment subscription was requested", 2);
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
                    List<UserDto> subscribers =
                        GetCardSubscribers(card, navigationCardsManager, userManager, recepient);

                    //if (!subscribers.Contains(recepient) && subscribers.Find(u => u.Entity.Id == recepient.Entity.Id) == null) subscribers.Insert(0, recepient);
                    if (card.CardData.Subscription.IndividualAlert)
                    {
                        foreach (var user in subscribers)
                        {
                            if (allOptOuts.Any(s =>
                                s.UserId == user.Entity.Id && s.CardId == card.Id &&
                                s.OptOutType == OptOutEmails.OptOutTypes.Alert))
                            {
                                LogDebugMessage($"User {user.Entity.Fullname} has opted out of this alert (card:{card.Id})",2);
                                continue;
                            }

                            foreach (var issue in individualIssues)
                            {
                                string individualDupeKey = string.Format("{0}-{1}", user.Entity.Id, issue.Entity.Id);

                                if (individualIssuesEmailedToUsers.Contains(individualDupeKey))
                                {
                                    LogDebugMessage($"The user has already received this combination - key:{individualDupeKey}",2);
                                    continue;
                                }

                                if (user != recepient)
                                {
                                    //var permissionManager = new PermissionsManager(user, _types, _permissionSets, _organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);
                                    var permissionManager = new PermissionsManager(user, Cache.Types,
                                        Cache.PermissionSets, Cache.Organizations,
                                        IssueManager.UserContext.Config.HelpDeskModeGroup, false);

                                    if (!permissionManager.CanSeeItem(issue.Project, issue))
                                    {
                                        LogDebugMessage($"The user {user.Entity.Fullname} does not have permission to view this item {issue.IssueKey}");
                                        continue;
                                    }

                                    issue.ChangeLog = IssueManager.GetChangeLog(issue, IssueManager.UserContext.User,
                                        user, lastCheckedLocal);
                                }

                                var indModel = new AlertTypeIndividualTemplateModel();

                                indModel.GeminiUrl = model.GeminiUrl;

                                indModel.LinkViewItem = NavigationHelper.GetIssueUrl(IssueManager.UserContext,
                                    issue.Entity.ProjectId, issue.EscapedProjectCode, issue.Entity.Id);

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
                                    issue.Entity.ProjectId);

                                string html = alerts.GenerateHtml(template, indModel);

                                if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                                // Send email
                                string log;

                                string subject = template.Options.Subject.HasValue()
                                    ? alerts.GenerateHtml(template, indModel, true)
                                    : string.Format("[{0}] {1} {2} ({3})", issue.IssueKey, issue.Type,
                                        model.TheItemsCreated.Contains(issue) ? "Created" : "Updated", issue.Title,
                                        issue.IsClosed ? "Closed" : string.Empty);

                                LogDebugMessage($"Sending email to {user.Entity.Email} subject: {subject}");
                                EmailHelper.Send(IssueManager.UserContext.Config, subject, html, user.Entity.Email,
                                    user.Fullname, true, out log);

                                individualIssuesEmailedToUsers.Add(individualDupeKey);
                            }
                        }
                    }
                    else
                    {
                        LogDebugMessage($"Batched emails requested");

                        var cloneCreated = new List<IssueDto>(model.TheItemsCreated);

                        var cloneUpdated = new List<IssueDto>(model.TheItemsUpdated);

                        var cloneCommented = new List<IssueDto>(model.TheItemsCommented);

                        // Find email template to use (for this project or fall back to default template)
                        AlertTemplate template = alerts.FindTemplateForProject(AlertTemplateType.AppNavAlerts, 0);

                        foreach (var user in subscribers)
                        {
                            if (allOptOuts.Any(s =>
                                s.UserId == user.Entity.Id && s.CardId == card.Id &&
                                s.OptOutType == OptOutEmails.OptOutTypes.Alert))
                            {
                                LogDebugMessage($"User {user.Entity.Fullname} has opted out of this alert (card:{card.Id})", 2);
                                continue;
                            }

                            model.TheItemsCreated = new List<IssueDto>(cloneCreated);

                            model.TheItemsUpdated = new List<IssueDto>(cloneUpdated);

                            model.TheItemsCommented = new List<IssueDto>(cloneCommented);

                            if (user != recepient)
                            {
                                //var permissionManager = new PermissionsManager(user, _types, _permissionSets, _organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);
                                var permissionManager = new PermissionsManager(user, Cache.Types, Cache.PermissionSets,
                                    Cache.Organizations, IssueManager.UserContext.Config.HelpDeskModeGroup, false);

                                model.TheItemsCreated.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                model.TheItemsUpdated.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                model.TheItemsCommented.RemoveAll(i => !permissionManager.CanSeeItem(i.Project, i));

                                foreach (var issue in model.TheItemsCreated.Concat(model.TheItemsUpdated)
                                    .Concat(model.TheItemsCommented))
                                {
                                    issue.ChangeLog = IssueManager.GetChangeLog(issue, IssueManager.UserContext.User,
                                        user, lastCheckedLocal);
                                }
                            }

                            //model.TheItemsCreated.RemoveAll(i => i.ChangeLog.Count == 0);
                            model.TheItemsUpdated.RemoveAll(i => i.ChangeLog.Count == 0);

                            model.TheItemsCommented.RemoveAll(i => i.ChangeLog.Count == 0);

                            if (model.ChangeCount == 0)
                            {
                                LogDebugMessage($"There were no changes visible for this user",2);
                                continue;
                            }

                            // Generate email template
                            string html = alerts.GenerateHtml(template, model);

                            if (GeminiApp.GeminiLicense.IsFree) html = alerts.AddSignature(html);

                            string subject = template.Options.Subject.HasValue()
                                ? alerts.GenerateHtml(template, model, true)
                                : string.Format("{0} {1}", card.Key, card.Title);

                            // Send email
                            string log;

                            LogDebugMessage($"Sending email to {user.Entity.Email} subject: {subject}");
                            EmailHelper.Send(IssueManager.UserContext.Config, subject, html, user.Entity.Email,
                                user.Fullname, true, out log);
                        }
                    }
                }
                else
                {
                    LogDebugMessage("No changes to show for this model",2);
                }

                // Remove the alert notifications and update the database
                lock (card.CardData.Alerts)
                {
                    card.CardData.Alerts.RemoveAll(a => issuesToAlert.Contains(a));
                }

                card.CardData.AlertsLastSent = DateTime.UtcNow;
                LogDebugMessage($"Updating the cards's last updated date to {card.CardData.AlertsLastSent}",2);

                refreshCache = true;

                navigationCardsManager.Update(card, false, false);
            }

            if (refreshCache)
            {
                LogDebugMessage($"Refreshing the navigation card cache");
                navigationCardsManager.Cache.NavigationCards.Invalidate();
                var webNodes = GeminiApp.Container.Resolve<IWebNodes>();
                webNodes.AddDataOnAllNodesButMe(new WebNodeData() { NodeGuid = GeminiApp.GUID, Key = "cache", Value = navigationCardsManager.Cache.NavigationCards.CacheKey });
            }
        }


        private List<UserDto> GetCardSubscribers(NavigationCard card, NavigationCardsManager cardManager, UserManager userManager, UserDto owner)
        {
            if (!owner.Entity.Active) return new List<UserDto>();

            Dictionary<int, UserDto> subscribers = new Dictionary<int, UserDto>();

            subscribers.Add(owner.Entity.Id, owner);

            foreach (var user in card.CardData.Subscription.Users)
            {
                var userDto = userManager.Get(user);

                if (user != owner.Entity.Id && userDto != null && userDto.Entity.Active) subscribers.Add(user, userDto);
            }

            var groupUsers = cardManager.GetUsersFromGroups(card, card.CardData.Subscription.Groups);

            foreach (var user in groupUsers)
            {
                if (!subscribers.ContainsKey(user.Entity.Id)) subscribers.Add(user.Entity.Id, user);
            }

            return new List<UserDto>(subscribers.Values);
        }



    }
}
