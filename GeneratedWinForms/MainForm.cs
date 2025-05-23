
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EliteDangerousStationManager
{
    public partial class MainForm : Form
    {
        private ConstructionProject project;

        public MainForm()
        {
            InitializeComponent();
            LoadProject();
        }

        private void LoadProject()
        {
            project = new ConstructionProject
            {
                Id = 1,
                ProjectName = "Raven Station",
                MaterialsRequired = new List<MaterialRequirement>
                {
                    new MaterialRequirement { Id = 1, MaterialName = "Iron", QuantityRequired = 100 },
                    new MaterialRequirement { Id = 2, MaterialName = "Copper", QuantityRequired = 50 }
                }
            };

            lblProjectName.Text = $"Project: {project.ProjectName}";

            lstMaterials.Items.Clear();
            foreach (var mat in project.MaterialsRequired)
            {
                lstMaterials.Items.Add($"{mat.MaterialName}: {mat.QuantityRequired}");
            }

            LogInfo($"Project '{project.ProjectName}' loaded with {project.MaterialsRequired.Count} materials.");
        }

        private void LogInfo(string message)
        {
            lblStatus.Text = $"Info: {message}";
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            LoadProject();
            LogInfo("Project reloaded.");
        }
    }
}
