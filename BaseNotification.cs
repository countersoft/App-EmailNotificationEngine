using System;
using System.Collections.Generic;
using System.IO;
using System.Web;
using Countersoft.Gemini.Infrastructure.Managers;

namespace EmailNotificationEngine
{
    public abstract class BaseNotification : INotificationAlert
    {
        protected IssueManager IssueManager { get; }
        protected readonly NotificationCache Cache;
        private StreamWriter _streamWriter;

        protected abstract void ProcessNotifications();

        public void Send()
        {
            try
            {
                if (CreateFileLog)
                {
                    using (_streamWriter = OpenFile())
                    {
                        ProcessNotifications();
                        CloseFile();
                    }
                }
                else
                {
                    ProcessNotifications();
                }

            }
            catch (Exception ex)
            {
                LogDebugMessage(ex.ToString());
            }
            finally
            {
                if (CreateFileLog)
                {
                    CloseFile();
                }
            }
        }

        public List<string> Log { get; set; } = new List<string>();
        public bool CreateFileLog { get; set; }

        protected void LogDebugMessage(string message, int level = 1)
        {
            Log.Add(message);
            if (level == 1)
            {
                MessageRaised(new NotificationMessageEventArgs(message, level));
            }
            if (CreateFileLog)
            {
                if (_streamWriter == null)
                {
                    OpenFile();
                }
                _streamWriter.WriteLine(message);
            }
        }

        protected void LogException(Exception ex)
        {
            Log.Add(ex.ToString());
            MessageRaised(new NotificationMessageEventArgs(ex.ToString()));
        }

        protected BaseNotification(NotificationCache cache, IssueManager issueManager)
        {
            IssueManager = issueManager;
            Cache = cache;
        }

        protected StreamWriter OpenFile()
        {
            var dir = Path.Combine(HttpRuntime.AppDomainAppPath, "app_data", "EmalProcessor");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var path = Path.Combine(dir,"EmailProcessorLog_"+ this.GetType().Name +".txt");
            if (File.Exists(path))
            {
                File.Copy(path, path.Replace(".txt", DateTime.Now.Ticks + ".txt"));
                File.Delete(path);
            }
            _streamWriter = File.CreateText(path);
            _streamWriter.AutoFlush = true;
            return _streamWriter;
        }


        protected void CloseFile()
        {
            try
            {
                _streamWriter.Flush();
                _streamWriter.Close();
            }
            catch (Exception)
            {
                // ignored
            }
        }



        public event EventHandler<NotificationMessageEventArgs> OnMessageRaised;
        public event EventHandler<NotificationMessageEventArgs> OnDebugMessageRaised;

        protected virtual void MessageRaised(NotificationMessageEventArgs e)
        {
            OnMessageRaised?.Invoke(this, e);
        }

        protected virtual void DebugMessageRaised(NotificationMessageEventArgs e)
        {
            OnDebugMessageRaised?.Invoke(this, e);
        }
    }
}