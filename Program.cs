﻿using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Reflection;
using Squared.Task;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Data.SQLite;
using Squared.Task.Data;
using Squared.Util.Event;

namespace ShootBlues {
    public class ScriptName {
        public readonly string Name;
        public readonly string DefaultSearchPath;
        private readonly string Invariant;

        public ScriptName (string name) {
            Name = name;
            DefaultSearchPath = null;
            Invariant = name.ToLowerInvariant();
        }

        public ScriptName (string name, string defaultSearchPath) {
            Name = name;
            DefaultSearchPath = defaultSearchPath;
            Invariant = name.ToLowerInvariant();
        }

        public static implicit operator string (ScriptName name) {
            if (name != null)
                return name.Name;
            else
                return null;
        }

        public string NameWithoutExtension {
            get {
                return Path.GetFileNameWithoutExtension(Name);
            }
        }

        public string Extension {
            get {
                return Path.GetExtension(Name).ToLower();
            }
        }

        public override int GetHashCode () {
            return Invariant.GetHashCode();
        }

        public override string ToString () {
            return Name;
        }

        public override bool Equals (object obj) {
            var sn = obj as ScriptName;
            if (sn != null)
                return Invariant.Equals(sn.Invariant);
            else {
                var fn = obj as Filename;
                if (fn != null)
                    return Invariant.Equals(fn.Name.Invariant);
                else {
                    var str = obj as String;
                    if (str != null)
                        return Invariant.Equals(str.ToLowerInvariant());
                    else
                        return base.Equals(obj);
                }
            }
        }
    }

    public class Filename {
        public readonly string FullPath;
        private readonly string Invariant;

        private Filename (string filename, bool resolved) {
            if (!resolved) {
                try {
                    FullPath = Path.GetFullPath(filename);
                } catch {
                    FullPath = filename;
                }
            } else
                FullPath = filename;

            Invariant = FullPath.ToLowerInvariant();
        }

        public Filename (string filename)
            : this(filename, false) {
        }

        public string Extension {
            get {
                return Path.GetExtension(FullPath).ToLower();
            }
        }

        public string NameWithoutExtension {
            get {
                return Path.GetFileNameWithoutExtension(FullPath);
            }
        }

        public ScriptName Name {
            get {
                return new ScriptName(
                    Path.GetFileName(FullPath),
                    Path.GetDirectoryName(FullPath)
                );
            }
        }

        public string Directory {
            get {
                return Path.GetDirectoryName(FullPath);
            }
        }

        public static implicit operator Filename (string filename) {
            return new Filename(filename, false);
        }

        public static implicit operator string (Filename filename) {
            if (filename != null)
                return filename.FullPath;
            else
                return null;
        }

        public override int GetHashCode () {
            return Invariant.GetHashCode();
        }

        public override string ToString () {
            return FullPath;
        }

        public override bool Equals (object obj) {
            var fn = obj as Filename;
            if (fn != null)
                return Invariant.Equals(fn.Invariant);
            else {
                var str = obj as String;
                if (str != null)
                    return Invariant.Equals(str.ToLowerInvariant());
                else
                    return base.Equals(obj);
            }
        }
    }

    public class ProcessInfo : IDisposable {
        public Process Process = null;
        public RPCChannel Channel = null;
        public string Status = "Unknown";

        internal HashSet<ScriptName> LoadedScripts = new HashSet<ScriptName>();
        private Dictionary<string, RPCResponseChannel> NamedChannels = new Dictionary<string, RPCResponseChannel>();
        private HashSet<IFuture> OwnedFutures = new HashSet<IFuture>();

        public ProcessInfo (Process process) {
            Process = process;
            Channel = new RPCChannel(process);
        }

        public RPCResponseChannel GetNamedChannel (string name) {
            RPCResponseChannel result;
            if (!NamedChannels.TryGetValue(name, out result))
                NamedChannels[name] = result = new RPCResponseChannel(Process);

            return result;
        }

        public IFuture Start (ISchedulable schedulable) {
            var f = Program.Scheduler.Start(
                schedulable, TaskExecutionPolicy.RunAsBackgroundTask
            );
            OwnedFutures.Add(f);
            return f;
        }

        public IFuture Start (IEnumerator<object> task) {
            var f = Program.Scheduler.Start(
                task, TaskExecutionPolicy.RunAsBackgroundTask
            );
            OwnedFutures.Add(f);
            return f;
        }

        public void Dispose () {
            foreach (var f in OwnedFutures)
                f.Dispose();
            OwnedFutures.Clear();

            foreach (var nc in NamedChannels.Values)
                nc.Dispose();
            NamedChannels.Clear();

            Channel.Dispose();
        }

        public override int GetHashCode () {
            return Process.Id.GetHashCode();
        }

        public override string ToString () {
            return String.Format("{0} - {1}", Process.Id, Status);
        }

        public override bool Equals (object obj) {
            var rhs = obj as ProcessInfo;
            if (rhs != null) {
                return Process.Id == rhs.Process.Id;
            } else {
                return base.Equals(obj);
            }
        }
    }

    public static class Program {
        private static StatusWindow StatusWindowInstance = null;
        private static ErrorDialog ErrorDialogInstance = null;
        private static ContextMenuStrip TrayMenu = null;
        private static Dictionary<ScriptName, SignalFuture> LoadingScripts = new Dictionary<ScriptName, SignalFuture>();
        private static HashSet<string> AttachedDatabases = new HashSet<string>();
        private static int ExitCode = 0;

        internal static Dictionary<ScriptName, IManagedScript> LoadedScripts = new Dictionary<ScriptName, IManagedScript>();

        public static readonly HashSet<ProcessInfo> RunningProcesses = new HashSet<ProcessInfo>();
        public static readonly HashSet<Filename> Scripts = new HashSet<Filename>();
        public static readonly HashSet<ScriptName> FailedScripts = new HashSet<ScriptName>();
        public static readonly EventBus EventBus = new EventBus();
        public static NotifyIcon TrayIcon;
        public static IProfile Profile;
        public static TaskScheduler Scheduler;
        public static ConnectionWrapper Database;

        [STAThread]
        private static void Main () {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (Scheduler = new TaskScheduler(JobQueue.WindowsMessageBased))
            using (Database = new ConnectionWrapper(Scheduler, OpenDatabase(), true)) {
                Scheduler.ErrorHandler = OnTaskError;

                using (var fMainTask = Scheduler.Start(MainTask(), TaskExecutionPolicy.RunAsBackgroundTask)) {
                    fMainTask.RegisterOnComplete((_) => {
                        if (_.Failed) {
                            ExitCode = 1;
                            Application.Exit();
                        }
                    });

                    Application.Run();
                    fMainTask.Dispose();
                }

                using (var fTeardownTask = Scheduler.Start(Teardown(), TaskExecutionPolicy.RunAsBackgroundTask))
                    Application.Run();
            }

            Environment.Exit(ExitCode);
        }

        private static SQLiteConnection OpenDatabase () {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ShootBlues"
            );
            if (!Directory.Exists(dataFolder))
                Directory.CreateDirectory(dataFolder);

            var dbPath = Path.Combine(
                dataFolder, "prefs.db"
            );
            var conn = new SQLiteConnection(String.Format("Data Source={0}", dbPath));
            conn.Open();

            return conn;
        }

        private static bool OnTaskError (Exception error) {
            ShowErrorMessage(error.ToString());

            return true;
        }

        public static void ShowErrorMessage (string text) {
            ShowErrorMessage(text, null);
        }

        public static void ShowErrorMessage (string text, ProcessInfo process) {
            string title = "Error in background task";
            if (process != null)
                title = String.Format("Error in process {0}", process.Process.Id);

            if (ErrorDialogInstance == null) {
                ErrorDialogInstance = new ErrorDialog(Scheduler);
                Scheduler.Start(ErrorDialogTask(), TaskExecutionPolicy.RunAsBackgroundTask);
            } else {
                ErrorDialogInstance.Activate();
                ErrorDialogInstance.Focus();
            }

            ErrorDialogInstance.AddError(text, title);
        }

        private static IEnumerator<object> ErrorDialogTask () {
            using (ErrorDialogInstance)
                yield return ErrorDialogInstance.Show();

            ErrorDialogInstance = null;
        }

        private static IEnumerator<object> MainTask () {
            TrayMenu = new ContextMenuStrip();
            TrayMenu.AddItem("&Status", (s, e) => Scheduler.Start(ShowStatusWindow(), TaskExecutionPolicy.RunAsBackgroundTask));
            TrayMenu.Items.Add("-");
            TrayMenu.AddItem("E&xit", (s, e) => Application.Exit());

            using (TrayMenu)
            using (var loadingWindow = new LoadingWindow())
            using (TrayIcon = new NotifyIcon {
                Text = "Shoot Blues v" + Application.ProductVersion,
                Icon = Properties.Resources.icon,
                ContextMenuStrip = TrayMenu
            }) {
                TrayIcon.DoubleClick += (s, e) => Scheduler.Start(ShowStatusWindow(), TaskExecutionPolicy.RunAsBackgroundTask);

                loadingWindow.SetStatus("Loading profile", null);
                loadingWindow.Show();

                yield return Database.ExecuteSQL("PRAGMA journal_mode = MEMORY");
                yield return Database.ExecuteSQL("PRAGMA temp_store = MEMORY");
                yield return Database.ExecuteSQL("PRAGMA synchronous = 0");

                RunToCompletion<IProfile> loadProfile = new RunToCompletion<IProfile>(
                    LoadProfile(loadingWindow), TaskExecutionPolicy.RunAsBackgroundTask
                );
                yield return loadProfile;

                using (Profile = loadProfile.Result)
                try {
                    yield return CreateDBTable("scripts", "( profileName TEXT NOT NULL, filename TEXT NOT NULL, PRIMARY KEY (profileName, filename) )");

                    string[] filenames = null;

                    yield return Database.ExecutePrimitiveArray<string>(
                        "SELECT filename FROM scripts WHERE profileName = ?", 
                        Profile.Name
                    ).Bind(() => filenames);

                    foreach (var filename in filenames)
                        Scripts.Add(filename);

                    loadingWindow.Close();

                    EventBus.Subscribe(Profile, "OnScriptsAdded", Scheduler, OnScriptsChanging);
                    EventBus.Subscribe(Profile, "OnScriptRemoved", Scheduler, OnScriptsChanging);

                    TrayIcon.Text = TrayIcon.Text + " - " + Profile.Name;
                    TrayIcon.Visible = true;

                    yield return OnScriptsChanging(null);

                    yield return Profile.Run();
                } finally {
                    TrayIcon.Visible = false;
                }
            }
        }

        public static IEnumerator<object> Teardown () {
            Console.WriteLine("Shutting down...");

            foreach (var process in RunningProcesses) {
                Console.WriteLine("Unloading scripts from process {0}...", process.Process.Id);

                foreach (var scriptName in process.LoadedScripts)
                    yield return LoadedScripts[scriptName].UnloadFrom(process);

                yield return Future.RunInThread(() =>
                    process.Channel.Send(new RPCMessage {
                        Type = RPCMessageType.ReloadModules
                    }));
                
                process.LoadedScripts.Clear();
                process.Dispose();
            }

            RunningProcesses.Clear();

            Console.WriteLine("Destroying scripts...");

            foreach (var script in LoadedScripts.Values)
                script.Dispose();

            LoadedScripts.Clear();

            Console.WriteLine("Done shutting down.");
            Application.Exit();
        }

        public static IEnumerator<object> AttachDB (string dbName) {
            var dbPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath),
                dbName + ".db"
            ));

            if (AttachedDatabases.Contains(dbName))
                yield break;

            AttachedDatabases.Add(dbName);

            yield return Database.ExecuteSQL(String.Format(
                "ATTACH DATABASE '{0}' AS {1}", dbPath, dbName
            ));
        }

        public static IEnumerator<object> DefaultTableConverter (string oldTableName, string newTableName, string oldTableSql, string newTableSql) {
            yield return Database.ExecuteSQL(String.Format(
                "INSERT INTO {0} SELECT * FROM {1}", newTableName, oldTableName
            ));
        }

        public static IEnumerator<object> CreateDBTable (string tableName, string tableDef) {
            return CreateDBTable(tableName, tableDef, DefaultTableConverter);
        }

        public static IEnumerator<object> CreateDBTable (string tableName, string tableDef, TableConverterTask tableConverter) {
            tableDef = tableDef.Trim();

            string sql = null;
            string newSql = String.Format("CREATE TABLE {0} {1}", tableName, tableDef);
            string oldName = String.Format("{0}_old", tableName);

            using (var q = Database.BuildQuery(@"SELECT sql FROM sqlite_master WHERE tbl_name = ? AND type = 'table'"))
                yield return q.ExecuteScalar<string>(tableName).Bind(() => sql);

            if (String.Compare(sql, newSql, true) != 0)
            using (var xact = Database.CreateTransaction(true)) {
                yield return xact;

                if (sql != null)
                    yield return Database.ExecuteSQL(String.Format("ALTER TABLE {0} RENAME TO {1}", tableName, oldName));

                yield return Database.ExecuteSQL(newSql);

                if (sql != null) {
                    yield return tableConverter(oldName, tableName, sql, newSql);

                    yield return Database.ExecuteSQL(String.Format("DROP TABLE {0}", oldName));
                }
                yield return xact.Commit();
            }
        }

        private static IEnumerator<object> LoadProfile (LoadingWindow loadingWindow) {
            IProfile instance = null;

            string profilePath = null;
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                    profilePath = args[1];
            }
            bool validProfile = false;
            try {
                validProfile = (profilePath != null) && File.Exists(profilePath);
            } catch {
            }

            while (instance == null) {
                if (!validProfile)
                using (var dialog = new OpenFileDialog()) {
                    dialog.Title = "Select Profile";
                    dialog.Filter = "Shoot Blues Profiles|*.profile.dll";
                    dialog.InitialDirectory = Path.GetDirectoryName(Application.ExecutablePath);

                    if (dialog.ShowDialog(loadingWindow) != DialogResult.OK) {
                        Application.Exit();
                        yield break;
                    }

                    profilePath = dialog.FileName;
                }

                var fAssembly = Future.RunInThread(() =>
                    Assembly.LoadFile(profilePath)
                );
                yield return fAssembly;

                var fTypes = Future.RunInThread(() => fAssembly.Result.GetTypes());
                yield return fTypes;

                var profileInterface = typeof(IProfile);
                foreach (var type in fTypes.Result) {
                    if (!profileInterface.IsAssignableFrom(type))
                        continue;

                    var constructor = type.GetConstructor(new Type[0]);
                    instance = constructor.Invoke(null) as IProfile;

                    break;
                }

                if (instance == null) {
                    validProfile = false;
                    Program.ShowErrorMessage(
                        String.Format("The file '{0}' is not a valid profile.", profilePath)
                    );
                }
            }

            yield return new Result(instance);
        }

        public static void AddCustomMenu (ToolStripMenuItem menu) {
            if (TrayMenu.Items.Count == 3)
                TrayMenu.Items.Insert(1, new ToolStripSeparator());
            TrayMenu.Items.Insert(TrayMenu.Items.Count - 2, menu);
        }

        public static void RemoveCustomMenu (ToolStripMenuItem menu) {
            TrayMenu.Items.Remove(menu);
            if (TrayMenu.Items.Count == 4)
                TrayMenu.Items.RemoveAt(1);
        }

        public static IEnumerator<object> ShowStatusWindow () {
            return ShowStatusWindow(null);
        }

        public static IEnumerator<object> ShowStatusWindow (string initialPage) {
            if (StatusWindowInstance != null) {
                StatusWindowInstance.Activate();
                StatusWindowInstance.Focus();

                if (initialPage != null)
                    StatusWindowInstance.SelectTab(initialPage);

                yield break;
            }

            using (StatusWindowInstance = new StatusWindow(Scheduler)) {
                var names = (from sn in LoadedScripts.Keys orderby sn.Name select sn).ToArray();
                foreach (var sn in names) {
                    var instance = LoadedScripts[sn];

                    var f = Scheduler.Start(
                        instance.OnStatusWindowShown(StatusWindowInstance), TaskExecutionPolicy.RunAsBackgroundTask
                    );
                    yield return f;
                    var temp = f.Failed;
                }

                if (initialPage != null)
                    StatusWindowInstance.SelectTab(initialPage);

                yield return StatusWindowInstance.Show();
            }

            StatusWindowInstance = null;
        }

        public static IEnumerator<object> BuildOrderedScriptList (LoadingWindow loadingWindow) {
            IManagedScript instance = null;
            var visited = new HashSet<ScriptName>();
            var result = new List<ScriptName>();
            var allScripts = new HashSet<ScriptName>(from fn in Scripts select fn.Name);
            var toVisit = new LinkedList<ScriptName>(allScripts);
            var majorScripts = new HashSet<ScriptName>(allScripts);

            int initialCount = majorScripts.Count;

            while (toVisit.Count > 0) {
                if (loadingWindow != null)
                    loadingWindow.SetProgress(
                        1.0f - (majorScripts.Count / (float)initialCount)
                    );

                var current = toVisit.PopFirst();

                if (result.Contains(current))
                    continue;

                visited.Add(current);
                majorScripts.Remove(current);

                yield return LoadScript(current, loadingWindow);

                if (!LoadedScripts.TryGetValue(current, out instance)) {
                    if (!FailedScripts.Contains(current))
                        FailedScripts.Add(current);

                    continue;
                } else {
                    if (!allScripts.Contains(current))
                        allScripts.Add(current);
                }

                var head = toVisit.First;

                bool resolved = true;

                foreach (var dep in instance.Dependencies) {
                    if (visited.Contains(dep))
                        continue;

                    if (head != null)
                        toVisit.AddBefore(head, dep);
                    else
                        toVisit.AddLast(dep);
                    resolved = false;
                }

                foreach (var dep in instance.OptionalDependencies) {
                    if (visited.Contains(dep))
                        continue;

                    if (!allScripts.Contains(dep))
                        continue;

                    if (head != null)
                        toVisit.AddBefore(head, dep);
                    else
                        toVisit.AddLast(dep);
                    resolved = false;
                }

                if (resolved) {
                    result.Add(current);
                } else {
                    if (head != null)
                        toVisit.AddBefore(head, current);
                    else
                        toVisit.AddLast(current);
                }
            }

            Console.WriteLine(
                "Dependency list: {0}",
                String.Join(", ", (from sn in result select sn.Name).ToArray())
            );
            yield return new Result(result.ToArray());
        }

        private static IEnumerator<object> SaveScriptsToDatabase () {
            using (var xact = Database.CreateTransaction(true)) {
                yield return xact;

                Database.ExecuteSQL("DELETE FROM scripts WHERE profileName = ?", Profile.Name);

                using (var q = Database.BuildQuery("INSERT INTO scripts (profileName, filename) VALUES (?, ?)"))
                foreach (var filename in Scripts)
                    yield return q.ExecuteNonQuery(Profile.Name, filename.FullPath);

                yield return xact.Commit();
            }
        }

        private static IEnumerator<object> OnScriptsChanging (Squared.Util.Event.EventInfo evt) {
            yield return new Start(SaveScriptsToDatabase(), TaskExecutionPolicy.RunAsBackgroundTask);

            using (var loadingWindow = new LoadingWindow()) {
                loadingWindow.SetStatus("Loading scripts", null);
                loadingWindow.Show();

                var buildScriptList = new RunToCompletion<ScriptName[]>(
                    BuildOrderedScriptList(loadingWindow), TaskExecutionPolicy.RunWhileFutureLives
                );
                yield return buildScriptList;

                var scriptList = buildScriptList.Result;

                var loadedScriptNames = LoadedScripts.Keys.ToArray();
                foreach (var scriptName in loadedScriptNames)
                    if (!scriptList.Contains(scriptName))
                        yield return UnloadScript(scriptName);

                foreach (var scriptName in scriptList)
                    if (!LoadedScripts.ContainsKey(scriptName))
                        yield return LoadScript(scriptName, loadingWindow);

                yield return ReloadAllScripts(scriptList);

                EventBus.Broadcast(Profile, "ScriptsChanged", scriptList); 
            }
        }

        public static IEnumerator<object> NotifyNewProcess (Process process) {
            var payload = Future.RunInThread(() => {
                using (var payloadStream = Assembly.GetExecutingAssembly().
                    GetManifestResourceStream("ShootBlues.payload.dll")) {
                    return new PortableExecutable(payloadStream);
                }
            });

            yield return payload;

            if (process.HasExited)
                yield break;

            var processExit = new SignalFuture();
            process.Exited += (s, e) => {
                processExit.Complete();
                process.EnableRaisingEvents = false;
            };
            process.EnableRaisingEvents = true;

            using (var pi = new ProcessInfo(process)) {
                var payloadResult = new Future<Int32>();
                var threadId = new Future<UInt32>();

                RunningProcesses.Add(pi);
                EventBus.Broadcast(Profile, "RunningProcessAdded", pi);

                var fCodeRegion = Future.RunInThread(() =>
                    ProcessInjector.Inject(process, payload.Result, pi.Channel.Handle, payloadResult, threadId)
                );
                yield return Future.WaitForFirst(
                    fCodeRegion, processExit
                );

                pi.Channel.RemoteThreadId = threadId.Result;

                using (fCodeRegion.Result) {
                    yield return Future.WaitForFirst(
                        pi.Channel.Receive(), processExit
                    );

                    pi.Status = "Loading scripts into process";
                    EventBus.Broadcast(Profile, "RunningProcessChanged", pi);

                    var buildScriptList = new RunToCompletion<ScriptName[]>(
                        BuildOrderedScriptList(null), TaskExecutionPolicy.RunWhileFutureLives
                    );
                    yield return buildScriptList;

                    if (!process.HasExited)
                        yield return Future.WaitForFirst(
                            Scheduler.Start(
                                LoadScriptsInto(pi, buildScriptList.Result),
                                TaskExecutionPolicy.RunAsBackgroundTask
                            ), processExit
                        );


                    if (!process.HasExited) {
                        pi.Status = "Scripts loaded";
                        EventBus.Broadcast(Profile, "RunningProcessChanged", pi);

                        var fRpcTask = Scheduler.Start(RPCTask(pi), TaskExecutionPolicy.RunWhileFutureLives);

                        using (fRpcTask)
                            yield return Future.WaitForFirst(
                                payloadResult, processExit
                            );

                        if (payloadResult.Completed) {
                            pi.Status = String.Format("Payload terminated with exit code {0}.", payloadResult.Result);
                            EventBus.Broadcast(Profile, "RunningProcessChanged", pi);
                        }
                    }
                }

                yield return processExit;
                RunningProcesses.Remove(pi);
                EventBus.Broadcast(Profile, "RunningProcessRemoved", pi);
            }
        }

        public static Filename FindScript (ScriptName script) {
            foreach (var filename in Scripts)
                if (script.Equals(filename))
                    return filename;

            if (script.DefaultSearchPath != null) {
                var candidatePath = Path.Combine(
                    script.DefaultSearchPath, script.Name
                );
                if (File.Exists(candidatePath))
                    return new Filename(candidatePath);
            }

            return null;
        }

        public static IEnumerator<object> FindScriptInteractive (ScriptName script, LoadingWindow loadingWindow) {
            var filename = FindScript(script);

            if (filename != null)
                yield return new Result(filename);

            if (FailedScripts.Contains(script) || (loadingWindow == null))
                yield return new Result(null);

            using (var dialog = new OpenFileDialog()) {
                dialog.Title = String.Format("Locate script '{0}'", script.Name);
                dialog.Filter = String.Format("{0}|{0}|All Scripts|*.script.dll;*.py", script.Name);
                dialog.InitialDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                if (dialog.ShowDialog(loadingWindow) != DialogResult.OK)
                    yield return new Result(null);
                else
                    yield return new Result(new Filename(
                        dialog.FileName
                    ));
            }
        }

        public static IEnumerator<object> LoadScript (ScriptName script, LoadingWindow loadingWindow) {
            IManagedScript instance = null;
            SignalFuture loadFuture;
            if (LoadingScripts.TryGetValue(script, out loadFuture)) {
                yield return loadFuture;
                yield break;
            } else if (LoadedScripts.TryGetValue(script, out instance)) {
                yield break;
            } else {
                LoadingScripts[script] = loadFuture = new SignalFuture();
            }

            var fScriptPath = new RunToCompletion<Filename>(
                FindScriptInteractive(script, loadingWindow), TaskExecutionPolicy.RunWhileFutureLives
            );
            yield return fScriptPath;
            var scriptPath = fScriptPath.Result;

            if (scriptPath == null) {
                instance = null;
            } else if (script.Extension == ".py") {
                instance = new PythonScript(scriptPath.Name);
            } else if (script.Extension == ".dll") {
                var fAssembly = Future.RunInThread(() =>
                    Assembly.LoadFile(scriptPath)
                );
                yield return fAssembly;

                var fTypes = Future.RunInThread(() => fAssembly.Result.GetTypes());
                yield return fTypes;

                var managedScript = typeof(IManagedScript);
                foreach (var type in fTypes.Result) {
                    if (!managedScript.IsAssignableFrom(type))
                        continue;

                    var constructor = type.GetConstructor(new Type[] { typeof(ScriptName) });
                    instance = constructor.Invoke(new object[] {
                        scriptPath.Name
                    }) as IManagedScript;

                    break;
                }
            }

            if (instance == null) {
                if (scriptPath != null)
                    Program.ShowErrorMessage(String.Format("The file '{0}' is not a Shoot Blues script.", scriptPath));
            } else {
                yield return instance.Initialize();

                LoadedScripts[script] = instance;

                if (StatusWindowInstance != null)
                    yield return instance.OnStatusWindowShown(StatusWindowInstance);
            }

            LoadingScripts.Remove(script);
            loadFuture.Complete();
        }

        public static IEnumerator<object> UnloadScript (ScriptName script) {
            IManagedScript instance = null;
            SignalFuture loadFuture;

            if (LoadingScripts.TryGetValue(script, out loadFuture)) {
                loadFuture.Dispose();
                LoadingScripts.Remove(script);
            } else if (LoadedScripts.TryGetValue(script, out instance)) {
                if (StatusWindowInstance != null)
                    yield return instance.OnStatusWindowHidden(StatusWindowInstance);

                foreach (var pi in RunningProcesses) {
                    if (pi.LoadedScripts.Contains(script)) {
                        yield return instance.UnloadFrom(pi);
                        pi.LoadedScripts.Remove(script);
                    }
                }

                instance.Dispose();
                LoadedScripts.Remove(script);
            }
        }

        public static IEnumerator<object> LoadPythonScript (ProcessInfo pi, string moduleName, string scriptText) {
            yield return Future.RunInThread(() =>
                pi.Channel.Send(new RPCMessage {
                    Type = RPCMessageType.AddModule,
                    ModuleName = moduleName,
                    Text = scriptText
                })
            );
        }

        public static IEnumerator<object> UnloadPythonScript (ProcessInfo pi, string moduleName) {
            yield return Future.RunInThread(() =>
                pi.Channel.Send(new RPCMessage {
                    Type = RPCMessageType.RemoveModule,
                    ModuleName = moduleName
                })
            );
        }

        public static Future<byte[]> EvalPython (ProcessInfo process, string pythonText) {
            var messageID = process.Channel.GetMessageID();
            var fResult = process.Channel.WaitForMessage(messageID);

            if (pythonText.Contains("\n") || pythonText.Contains("return ") || pythonText.StartsWith("print "))
                pythonText = "  " + pythonText.Replace("\t", "  ").Replace("\n", "\n  ");
            else
                pythonText = "  return " + pythonText;

            pythonText = String.Format(
                @"def __eval__():
{0}
result = __eval__()
if result:
  import json
  result = json.dumps(result)
from shootblues import rpcSend
rpcSend(result, id={1}L)", pythonText, messageID
            );

            Future.RunInThread(() =>
                process.Channel.Send(new RPCMessage {
                    Type = RPCMessageType.Run,
                    Text = pythonText
                })
            );

            return fResult;
        }

        public static Future<byte[]> CallFunction (ProcessInfo process, string moduleName, string functionName, params object[] arguments) {
            if ((arguments != null) && (arguments.Length == 0))
                arguments = null;

            string argsJson = null;
            if (arguments != null) {
                var serializer = new JavaScriptSerializer();
                argsJson = serializer.Serialize(arguments);
            }

            return process.Channel.Send(new RPCMessage {
                Type = RPCMessageType.CallFunction,
                ModuleName = moduleName,
                FunctionName = functionName,
                Text = argsJson
            }, true);
        }

        public static Future<T> CallFunction<T> (ProcessInfo process, string moduleName, string functionName, params object[] arguments) {
            var f = new Future<T>();
            var serializer = new JavaScriptSerializer();

            var inner = CallFunction(process, moduleName, functionName, arguments);
            inner.RegisterOnComplete((_) => {
                if (!inner.Failed) {
                    try {
                        var json = inner.Result.DecodeUTF8Z();
                        f.Complete(serializer.Deserialize<T>(json));
                    } catch (Exception ex) {
                        f.Fail(ex);
                    }
                } else
                    f.Fail(inner.Error);
            });
            inner.RegisterOnDispose((_) => f.Dispose());

            return f;
        }

        public static IEnumerator<object> ReloadAllScripts () {
            var buildScriptList = new RunToCompletion<ScriptName[]>(
                BuildOrderedScriptList(null), TaskExecutionPolicy.RunWhileFutureLives
            );
            yield return buildScriptList;
            var scriptList = buildScriptList.Result;

            yield return ReloadAllScripts(scriptList);
        }

        private static IEnumerator<object> ReloadAllScripts (ScriptName[] scriptList) {
            foreach (var pi in RunningProcesses) {
                foreach (var scriptName in scriptList.Reverse()) {
                    if (pi.LoadedScripts.Contains(scriptName)) {
                        yield return LoadedScripts[scriptName].UnloadFrom(pi);
                        pi.LoadedScripts.Remove(scriptName);
                    }
                }
            }

            foreach (var scriptName in scriptList)
                yield return LoadedScripts[scriptName].Reload();

            foreach (var pi in RunningProcesses)
                yield return LoadScriptsInto(pi, scriptList);
        }

        public static IEnumerator<object> LoadScriptsInto (ProcessInfo pi, ScriptName[] scriptList) {
            foreach (var script in scriptList) {
                yield return LoadedScripts[script].LoadInto(pi);
                pi.LoadedScripts.Add(script);
            }

            yield return Future.RunInThread(() =>
                pi.Channel.Send(new RPCMessage {
                    Type = RPCMessageType.ReloadModules
                }));

            foreach (var script in scriptList)
                yield return LoadedScripts[script].LoadedInto(pi);
        }

        private static IEnumerator<object> RPCTask (ProcessInfo pi) {
            while (true) {
                var fMessage = pi.Channel.Receive();
                yield return fMessage;

                var errorText = fMessage.Result.DecodeAsciiZ();
                Program.ShowErrorMessage(errorText, pi);
            }
        }

        public static IManagedScript GetScriptInstance (ScriptName script) {
            IManagedScript instance = null;
            LoadedScripts.TryGetValue(script, out instance);
            return instance;
        }

        public static T GetScriptInstance<T> (string scriptName)
            where T : class, IManagedScript {

            var instance = GetScriptInstance(new ScriptName(scriptName));
            return instance as T;
        }
    }

    public static class Extensions {
        public static void AddItem (this ContextMenuStrip menu, string text, EventHandler onClick) {
            var newItem = menu.Items.Add(text);
            newItem.Click += onClick;
        }

        public static string DecodeAsciiZ (this byte[] buffer) {
            if ((buffer == null) || (buffer.Length == 0))
                return null;

            int firstNull = Array.IndexOf(buffer, (byte)0, 0);
            return Encoding.ASCII.GetString(buffer, 0, firstNull);
        }

        public static string DecodeUTF8Z (this byte[] buffer) {
            if ((buffer == null) || (buffer.Length == 0))
                return null;

            int firstNull = Array.IndexOf(buffer, (byte)0, 0);
            return Encoding.UTF8.GetString(buffer, 0, firstNull);
        }

        public static T PopFirst<T> (this LinkedList<T> list) {
            var result = list.First.Value;
            list.RemoveFirst();
            return result;
        }
    }
}
