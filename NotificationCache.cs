using System.Collections.Generic;
using System.Linq;
using Countersoft.Gemini;
using Countersoft.Gemini.Commons;
using Countersoft.Gemini.Commons.Dto;
using Countersoft.Gemini.Commons.Entity;
using Countersoft.Gemini.Commons.Entity.Security;
using Countersoft.Gemini.Contracts.Business;
using Countersoft.Gemini.Infrastructure.Managers;
using Microsoft.Practices.Unity;

namespace EmailNotificationEngine
{
    public class NotificationCache
    {
        public List<AlertTemplate> Templates { get; }
        public List<IssueTypeDto> Types { get; }
        public List<PermissionSetDto> PermissionSets { get; }
        public List<Organization> Organizations { get; }

        private IssueManager _issueManager;
        public string BaseUrl { get; }

        public NotificationCache(IssueManager issueManager, string baseUrl)
        {
            _issueManager = issueManager;
            BaseUrl = baseUrl;

            Templates = GeminiApp.Container.Resolve<IAlertTemplates>().FindWhere(c => c.AlertType != AlertTemplateType.Breeze).ToList();

            Types = new MetaManager(issueManager).TypeGetAll();

            PermissionSets = new PermissionSetManager(issueManager).GetAll();

            Organizations = new OrganizationManager(issueManager).GetAll();
        }
    }
}