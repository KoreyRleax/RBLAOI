using RBLAOI.Core.Utility;
using RBLAOI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace RBLAOI.Core.Managers
{
    public class ProjectManager
    {
        private static ProjectManager _instance;
        private static readonly object _lock = new object();

        public static ProjectManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ProjectManager();
                    }
                }
                return _instance;
            }
        }

        private ProjectManager()
        {
            // 加载上次保存的路径
            LoadLastPath();
        }

        /// <summary>
        /// 用户上次操作的目录
        /// </summary>
        private string _lastDirectory;

        /// <summary>
        /// 默认目录（Debug/Board）
        /// </summary>
        private string DefaultDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Board");

        /// <summary>
        /// 获取当前工作目录（上次目录 或 默认目录）
        /// </summary>
        public string WorkingDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(_lastDirectory) && Directory.Exists(_lastDirectory))
                    return _lastDirectory;
                return DefaultDirectory;
            }
        }

        /// <summary>
        /// 当前加载的方案数据
        /// </summary>
        public ProjectData CurrentProject { get; private set; }

        /// <summary>
        /// 当前方案的文件夹路径
        /// </summary>
        public string CurrentProjectPath { get; private set; }

        /// <summary>
        /// 更新上次使用的目录
        /// </summary>
        private void UpdateLastDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            // 如果是文件，取目录
            if (File.Exists(path))
                _lastDirectory = Path.GetDirectoryName(path);
            else if (Directory.Exists(path))
                _lastDirectory = path;

            SaveLastPath();
        }

        public void SetWorkingDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                _lastDirectory = path;
                SaveLastPath();
            }
        }

        /// <summary>
        /// 保存路径到配置文件
        /// </summary>
        private void SaveLastPath()
        {
            try
            {
                string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                string pathFile = Path.Combine(configDir, "LastProjectPath.txt");
                File.WriteAllText(pathFile, _lastDirectory ?? DefaultDirectory);
            }
            catch { }
        }

        /// <summary>
        /// 加载上次保存的路径
        /// </summary>
        private void LoadLastPath()
        {
            try
            {
                string pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "LastProjectPath.txt");
                if (File.Exists(pathFile))
                {
                    _lastDirectory = File.ReadAllText(pathFile).Trim();
                }
            }
            catch { }
        }

        /// <summary>
        /// 获取所有方案列表
        /// </summary>
        public List<string> GetAllProjects()
        {
            var projects = new List<string>();
            string dir = DefaultDirectory;
            if (!Directory.Exists(dir)) return projects;

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                string configFile = Path.Combine(subDir, "ProjectConfig.xml");
                if (File.Exists(configFile))
                {
                    projects.Add(Path.GetFileName(subDir));
                }
            }
            return projects;
        }
     

        /// <summary>
        /// 创建新方案（始终在 Board 目录下）
        /// </summary>
        public bool CreateNewProject(string projectName)
        {
            try
            {
                string baseDir = DefaultDirectory;
                if (!Directory.Exists(baseDir))
                    Directory.CreateDirectory(baseDir);

                string projectDir = Path.Combine(baseDir, projectName);

                if (Directory.Exists(projectDir))
                {
                    projectName = $"{projectName}_{DateTime.Now:yyyyMMddHHmmss}";
                    projectDir = Path.Combine(baseDir, projectName);
                }

                Directory.CreateDirectory(projectDir);
                Directory.CreateDirectory(Path.Combine(projectDir, "Images"));
                Directory.CreateDirectory(Path.Combine(projectDir, "Positions"));
                Directory.CreateDirectory(Path.Combine(projectDir, "VMSolution"));

                // 从程序目录复制 VM 模板方案到新方案的 VMSolution 目录
                try
                {
                    string templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VmSolution", "Check.sol");
                    string targetPath = Path.Combine(projectDir, "VMSolution", "Check.sol");
                    if (File.Exists(templatePath))
                    {
                        File.Copy(templatePath, targetPath, overwrite: false);
                        Log.Info($"已复制VM模板方案: {targetPath}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"复制VM模板方案失败: {ex.Message}");
                }

                var project = new ProjectData
                {
                    ProjectName = projectName,
                    CreateTime = DateTime.Now,
                    ModifyTime = DateTime.Now
                };

                SaveProject(project, projectDir);
                CurrentProject = project;
                CurrentProjectPath = projectDir;

                // ✅ 不更新 _lastDirectory，保持"上次打开"路径不变

                Log.Info($"方案创建成功: {projectName}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"创建方案失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 保存上次加载的方案路径
        /// </summary>
        public void SaveLastLoadedProject(string projectName, string projectPath)
        {
            try
            {
                string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                string lastProjectFile = Path.Combine(configDir, "LastProject.txt");
                // 保存方案名称和方案文件夹路径，用 "|" 分隔
                File.WriteAllText(lastProjectFile, $"{projectName}|{projectPath}");
            }
            catch { }
        }

        /// <summary>
        /// 获取上次加载的方案信息（返回 null 表示没有记录）
        /// </summary>
        public (string projectName, string projectPath)? GetLastLoadedProject()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "LastProject.txt");
                if (File.Exists(path))
                {
                    string[] parts = File.ReadAllText(path).Split('|');
                    if (parts.Length == 2)
                    {
                        string projectName = parts[0];
                        string projectPath = parts[1];
                        // 检查文件夹是否还存在
                        if (Directory.Exists(projectPath))
                        {
                            return (projectName, projectPath);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 加载方案
        /// </summary>
        public bool LoadProject(string projectName)
        {
            try
            {
                string projectDir = Path.Combine(WorkingDirectory, projectName);
                string configFile = Path.Combine(projectDir, "ProjectConfig.xml");

                if (!File.Exists(configFile))
                {
                    Log.Error($"方案配置文件不存在: {configFile}");
                    return false;
                }

                XmlSerializer serializer = new XmlSerializer(typeof(ProjectData));
                using (StreamReader reader = new StreamReader(configFile))
                {
                    CurrentProject = (ProjectData)serializer.Deserialize(reader);
                }

                CurrentProjectPath = projectDir;
                UpdateLastDirectory(projectDir);
                Log.Info($"方案加载成功: {projectName}");

                SaveLastLoadedProject(projectName, projectDir);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"加载方案失败: {ex.Message}");
                CurrentProject = null;
                CurrentProjectPath = null;
                return false;
            }
        }

        /// <summary>
        /// 保存当前方案
        /// </summary>
        public bool SaveCurrentProject()
        {
            if (CurrentProject == null || string.IsNullOrEmpty(CurrentProjectPath))
            {
                Log.Error("没有加载的方案");
                return false;
            }

            CurrentProject.ModifyTime = DateTime.Now;
            return SaveProject(CurrentProject, CurrentProjectPath);
        }

        /// <summary>
        /// 另存为新方案
        /// </summary>
        public bool SaveAsProject(string newName)
        {
            if (CurrentProject == null) return false;

            string newDir = Path.Combine(WorkingDirectory, newName);
            if (Directory.Exists(newDir))
            {
                newName = $"{newName}_{DateTime.Now:yyyyMMddHHmmss}";
                newDir = Path.Combine(WorkingDirectory, newName);
            }

            Directory.CreateDirectory(newDir);
            Directory.CreateDirectory(Path.Combine(newDir, "Images"));
            Directory.CreateDirectory(Path.Combine(newDir, "Positions"));

            CurrentProject.ProjectName = newName;
            CurrentProject.ModifyTime = DateTime.Now;
            CurrentProjectPath = newDir;
            UpdateLastDirectory(newDir);

            return SaveProject(CurrentProject, newDir);
        }

        /// <summary>
        /// 删除方案
        /// </summary>
        public bool DeleteProject(string projectName)
        {
            try
            {
                string projectDir = Path.Combine(DefaultDirectory, projectName);
                if (Directory.Exists(projectDir))
                {
                    Directory.Delete(projectDir, true);

                    if (CurrentProject?.ProjectName == projectName)
                    {
                        CurrentProject = null;
                        CurrentProjectPath = null;
                    }

                    Log.Info($"方案已删除: {projectName}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"删除方案失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重命名方案（同时重命名文件夹，重新加载以更新状态）
        /// </summary>
        public bool RenameProject(string oldName, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newName)) return false;
                string oldDir = Path.Combine(DefaultDirectory, oldName);
                string newDir = Path.Combine(DefaultDirectory, newName);

                if (!Directory.Exists(oldDir)) return false;
                if (Directory.Exists(newDir))
                {
                    Log.Warning($"目标名称已存在: {newName}");
                    return false;
                }

                // 先更新配置文件（文件夹移动前），失败则不移动目录
                string configFile = Path.Combine(oldDir, "ProjectConfig.xml");
                if (File.Exists(configFile))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(ProjectData));
                    ProjectData data;
                    using (StreamReader reader = new StreamReader(configFile))
                    {
                        data = (ProjectData)serializer.Deserialize(reader);
                    }
                    data.ProjectName = newName;
                    data.ModifyTime = DateTime.Now;
                    using (StreamWriter writer = new StreamWriter(configFile))
                    {
                        serializer.Serialize(writer, data);
                    }
                }

                Directory.Move(oldDir, newDir);

                // 如果重命名的是当前方案，更新状态
                if (CurrentProject?.ProjectName == oldName)
                {
                    CurrentProject = null;
                    CurrentProjectPath = null;
                    LoadProject(newName);
                }

                Log.Info($"方案已重命名: {oldName} -> {newName}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"重命名方案失败: {ex.Message}");
                return false;
            }
        }

        private bool SaveProject(ProjectData project, string projectDir)
        {
            try
            {
                string configFile = Path.Combine(projectDir, "ProjectConfig.xml");
                XmlSerializer serializer = new XmlSerializer(typeof(ProjectData));
                using (StreamWriter writer = new StreamWriter(configFile))
                {
                    serializer.Serialize(writer, project);
                }
                Log.Info($"方案已保存: {configFile}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"保存方案失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取所有方案详细信息（含修改时间和备注），按修改时间降序
        /// </summary>
        public List<ProjectInfo> GetAllProjectInfos()
        {
            var results = new List<ProjectInfo>();
            string dir = DefaultDirectory;
            if (!Directory.Exists(dir)) return results;

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                string configFile = Path.Combine(subDir, "ProjectConfig.xml");
                if (!File.Exists(configFile)) continue;

                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(ProjectData));
                    using (StreamReader reader = new StreamReader(configFile))
                    {
                        var data = (ProjectData)serializer.Deserialize(reader);
                        results.Add(new ProjectInfo
                        {
                            ProjectName = data.ProjectName,
                            ProjectPath = subDir,
                            ModifyTime = data.ModifyTime,
                            Remark = data.Remark ?? ""
                        });
                    }
                }
                catch { }
            }

            results.Sort((a, b) => b.ModifyTime.CompareTo(a.ModifyTime));
            return results;
        }

        public string GetImagePath(string subFolder = "Images")
        {
            if (string.IsNullOrEmpty(CurrentProjectPath)) return null;
            string path = Path.Combine(CurrentProjectPath, subFolder);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public string GetOriginPath() => GetImagePath(System.IO.Path.Combine("Images", "Origin"));
        public string GetRenderPath() => GetImagePath(System.IO.Path.Combine("Images", "Render"));
        /// <summary>获取Pin针尖图片存储目录 ({方案目录}/Images/Pin)</summary>
        public string GetPinPath() => GetImagePath(System.IO.Path.Combine("Images", "Pin"));

        /// <summary>获取当前方案对应的VM方案文件路径 ({方案目录}/VMSolution/Check.sol)</summary>
        public string GetVmSolutionPath()
        {
            if (string.IsNullOrEmpty(CurrentProjectPath)) return null;
            return Path.Combine(CurrentProjectPath, "VMSolution", "Check.sol");
        }

        public void CloseProject()
        {
            CurrentProject = null;
            CurrentProjectPath = null;
        }
    }

    public class ProjectInfo
    {
        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }
        public DateTime ModifyTime { get; set; }
        public string Remark { get; set; }
    }
}