using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using NAppUpdate.Framework.Common;
using NAppUpdate.Framework.Conditions;
using NAppUpdate.Framework.Sources;
using NAppUpdate.Framework.Utils;

namespace NAppUpdate.Framework.Tasks
{
    [Serializable]
    [UpdateTaskAlias("fileDelete")]
    public class FileDeleteTask : UpdateTaskBase
    {
        [NauField("localPath", "The local path of the file to delete", true)]
        public string LocalPath { get; set; }

        private string _localFile, _backupFile;
        public override void Prepare(IUpdateSource source)
        {
            //nothing to prepare.
        }

        public override TaskExecutionStatus Execute(bool coldRun)
        {
            if (string.IsNullOrEmpty(LocalPath))
            {
                UpdateManager.Instance.Logger.Log(Logger.SeverityLevel.Warning, "FileDeleteTask: LocalPath is empty, task is a noop");
                return TaskExecutionStatus.Successful; // Errorneous case, but there's nothing to do, and by default we prefer a noop over an error
            }
            if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(UpdateManager.Instance.Config.BackupFolder, LocalPath))))
                Utils.FileSystem.CreateDirectoryStructure(Path.GetDirectoryName(Path.Combine(UpdateManager.Instance.Config.BackupFolder, LocalPath)), false);

            _localFile = Path.Combine(UpdateManager.Instance.Config.DirectoryToUpdate, LocalPath);
            _backupFile = Path.Combine(UpdateManager.Instance.Config.BackupFolder, LocalPath);
            File.Copy(_localFile, _backupFile, true);

            if (!PermissionsCheck.HaveWritePermissionsForFileOrFolder(_localFile))
            {
                return TaskExecutionStatus.RequiresPrivilegedAppRestart;
            }

            File.Delete(_localFile);
            return TaskExecutionStatus.Successful;
        }

        public override bool Rollback()
        {
            if (string.IsNullOrEmpty(_localFile))
                return true;

            // Copy the backup copy back to its original position
            if (File.Exists(_localFile))
                File.Delete(_localFile);
            File.Copy(_backupFile, _localFile, true);

            return true;
        }
    }
}