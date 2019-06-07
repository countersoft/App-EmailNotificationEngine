using System;
using System.Collections.Generic;
using System.Text;
using Countersoft.Gemini.Infrastructure.Managers;

namespace EmailNotificationEngine
{
    public interface INotificationAlert
    {
        void Send();
        List<string> Log { get; set; }
        bool CreateFileLog { get; set; }
        event EventHandler<NotificationMessageEventArgs> OnMessageRaised;
    }

    public class NotificationMessageEventArgs : EventArgs
    {
        public NotificationMessageEventArgs(string message, int level=1)
        {
            Message = message;
            Level = level;
        }
        public string Message { get;  }
        public int Level { get; }
    }

    public static class AlertFactory
    {
        public static IList<INotificationAlert> GetAlerters(NotificationCache cache, IssueManager issueManager)
        {
            return  new List<INotificationAlert>
            {
                new WorkspaceNotification(cache, issueManager),
                new FollowerNotification(cache, issueManager)
            };
        }
    }

    public enum ENotificationType
    {
        Workspace = 1,
        Follower = 2
    }

}
