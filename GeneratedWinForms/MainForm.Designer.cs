
namespace EliteDangerousStationManager
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Label lblProjectName;
        private System.Windows.Forms.ListBox lstMaterials;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Button btnRefresh;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblProjectName = new System.Windows.Forms.Label();
            this.lstMaterials = new System.Windows.Forms.ListBox();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblProjectName
            // 
            this.lblProjectName.AutoSize = true;
            this.lblProjectName.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold);
            this.lblProjectName.Location = new System.Drawing.Point(12, 9);
            this.lblProjectName.Name = "lblProjectName";
            this.lblProjectName.Size = new System.Drawing.Size(140, 25);
            this.lblProjectName.TabIndex = 0;
            this.lblProjectName.Text = "Project: None";
            // 
            // lstMaterials
            // 
            this.lstMaterials.FormattingEnabled = true;
            this.lstMaterials.ItemHeight = 15;
            this.lstMaterials.Location = new System.Drawing.Point(17, 46);
            this.lstMaterials.Name = "lstMaterials";
            this.lstMaterials.Size = new System.Drawing.Size(310, 124);
            this.lstMaterials.TabIndex = 1;
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(17, 182);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(39, 15);
            this.lblStatus.TabIndex = 2;
            this.lblStatus.Text = "Status";
            // 
            // btnRefresh
            // 
            this.btnRefresh.Location = new System.Drawing.Point(252, 179);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(75, 23);
            this.btnRefresh.TabIndex = 3;
            this.btnRefresh.Text = "Reload";
            this.btnRefresh.UseVisualStyleBackColor = true;
            this.btnRefresh.Click += new System.EventHandler(this.btnRefresh_Click);
            // 
            // MainForm
            // 
            this.ClientSize = new System.Drawing.Size(344, 214);
            this.Controls.Add(this.btnRefresh);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.lstMaterials);
            this.Controls.Add(this.lblProjectName);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.Text = "Elite Dangerous Station Manager";
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
