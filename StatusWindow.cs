﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Squared.Task;
using System.IO;

namespace ShootBlues {
    public partial class StatusWindow : TaskForm, IStatusWindow {
        public StatusWindow (TaskScheduler scheduler)
            : base(scheduler) {
            InitializeComponent();

            Text = Text.Replace("$version", String.Format("v{0}", Application.ProductVersion));
        }

        public IEnumerator<object> ShowProcessList () {
            while (true) {
                RunningProcessList.BeginUpdate();
                RunningProcessList.Items.Clear();
                foreach (var pi in Program.RunningProcesses)
                    RunningProcessList.Items.Add(pi);
                RunningProcessList.EndUpdate();

                yield return Program.RunningProcessesChanged.Wait();
            }
        }

        public IEnumerator<object> ShowScriptList () {
            Filename selectedScript;

            while (true) {
                if (ScriptsList.SelectedItems.Count > 0)
                    selectedScript = ScriptsList.SelectedItem as Filename;
                else
                    selectedScript = null;

                ScriptsList.BeginUpdate();
                ScriptsList.Items.Clear();
                foreach (var script in Program.Scripts)
                    ScriptsList.Items.Add(script);
                try {
                    ScriptsList.SelectedItem = selectedScript;
                } catch {
                    ScriptsList.SelectedItem = null;
                }
                ScriptsList.EndUpdate();

                UnloadScriptButton.Enabled = (ScriptsList.SelectedItem != null);

                yield return Program.ScriptsChanged.Wait();
            }
        }

        private void RunPythonMenu_Click (object sender, EventArgs e) {
            var process = (ProcessInfo)ProcessMenu.Tag;

            using (var dialog = new EnterPythonDialog())
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    Start(DoEval(process, dialog.PythonText.Text));
        }

        private IEnumerator<object> DoEval (ProcessInfo process, string pythonText) {
            var f = Program.EvalPython(process, pythonText);
            yield return f;
            byte[] result = f.Result;
            if ((result != null) && (result.Length > 0))
                MessageBox.Show(Encoding.ASCII.GetString(result), "Result");
        }

        private void RunningProcessList_MouseDown (object sender, MouseEventArgs e) {
            var index = RunningProcessList.IndexFromPoint(e.X, e.Y);
            if (index == ListBox.NoMatches)
                return;

            RunningProcessList.SelectedIndex = index;

            if (e.Button == MouseButtons.Right) {
                ProcessMenu.Tag = RunningProcessList.Items[index];
                ProcessMenu.Show(RunningProcessList, new Point(e.X, e.Y));
            }
        }

        private void LoadScriptButton_Click (object sender, EventArgs e) {
            using (var dialog = new OpenFileDialog()) {
                dialog.Title = "Load Script";
                dialog.Filter = "All Scripts|*.dll;*.py|Managed Scripts|*.dll|Python Scripts|*.py";
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                Start(AddScripts(new string[] { dialog.FileName }));
            }
        }

        private void StatusWindow_Shown (object sender, EventArgs e) {
            Start(ShowProcessList());
            Start(ShowScriptList());
        }

        private void ScriptsList_DragOver (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void ScriptsList_DragDrop (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop)) {
                string[] files = e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) as string[];
                if (files == null)
                    return;

                Start(AddScripts(
                    from file in files where (
                        (Path.GetExtension(file).ToLower() == ".py") ||
                        (Path.GetExtension(file).ToLower() == ".dll")
                    ) select file
                ));
            }
        }

        private IEnumerator<object> AddScripts (IEnumerable<string> filenames) {
            foreach (var filename in filenames)
                Program.Scripts.Add(filename);
            Program.ScriptsChanged.Set();

            foreach (var pi in Program.RunningProcesses) {
                foreach (var filename in filenames)
                    yield return Program.LoadScriptFromFilename(pi, filename);

                yield return Program.ReloadModules(pi);
            }
        }

        private void ReloadAllButton_Click (object sender, EventArgs e) {
            Start(ReloadAllScripts());
        }

        private IEnumerator<object> ReloadAllScripts () {
            foreach (var pi in Program.RunningProcesses) {
                foreach (var script in Program.Scripts)
                    yield return Program.LoadScriptFromFilename(pi, script);

                yield return Program.ReloadModules(pi);
            }
        }

        private void ScriptsList_SelectedIndexChanged (object sender, EventArgs e) {
            UnloadScriptButton.Enabled = (ScriptsList.SelectedItem != null);
        }

        private void UnloadScriptButton_Click (object sender, EventArgs e) {
            Start(RemoveScript(ScriptsList.SelectedItem as Filename));
        }

        private IEnumerator<object> RemoveScript (string filename) {
            Program.Scripts.Remove(filename);

            foreach (var pi in Program.RunningProcesses) {
                yield return Program.UnloadScriptFromFilename(pi, filename);

                yield return Program.ReloadModules(pi);
            }

            Program.ScriptsChanged.Set();
        }

        public TabPage ShowConfigurationPanel (string title, Control panel) {
            TabPage tabPage = panel as TabPage;
            if (tabPage == null) {
                tabPage = new TabPage();
                tabPage.Controls.Add(panel);
                panel.Dock = DockStyle.Fill;
            }

            tabPage.Text = title;
            tabPage.Name = title;
            Tabs.TabPages.Add(tabPage);
            return tabPage;
        }

        public void HideConfigurationPanel (TabPage page) {
            Tabs.TabPages.Remove(page);
        }

        public void HideConfigurationPanel (string title) {
            Tabs.TabPages.RemoveByKey(title);
        }
    }
}
