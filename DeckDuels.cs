using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeckDuels
{
    public partial class DeckDuelsForm : Form
    {
        private System.Windows.Forms.Timer gameTimer;
        private int timeLeft = 60;
        private int playerWeight = 50; // Starts in the middle, 100 means you overload/lose
        
        private Label lblTimer;
        private Label lblStatus;
        private ProgressBar pbScale;
        private Button btnPlayCard;

        public DeckDuelsForm()
        {
            // Basic Window Setup
            this.Text = "DeckDuels - 1v1 Overload";
            this.Size = new Size(400, 350);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(20, 20, 30);

            // Timer Label
            lblTimer = new Label()
            {
                Text = "Time: 60s",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Location = new Point(130, 20),
                AutoSize = true
            };
            this.Controls.Add(lblTimer);

            // Scale Progress Bar (Tug of War)
            pbScale = new ProgressBar()
            {
                Location = new Point(50, 80),
                Size = new Size(280, 30),
                Minimum = 0,
                Maximum = 100,
                Value = playerWeight
            };
            this.Controls.Add(pbScale);

            // Status / Instructions
            lblStatus = new Label()
            {
                Text = "Dump your weight before time runs out!",
                ForeColor = Color.Cyan,
                Font = new Font("Segoe UI", 10),
                Location = new Point(70, 130),
                AutoSize = true
            };
            this.Controls.Add(lblStatus);

            // Play Card Button
            btnPlayCard = new Button()
            {
                Text = "Play Card (+10 Weight)",
                Location = new Point(100, 180),
                Size = new Size(180, 40),
                BackColor = Color.FromArgb(40, 40, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnPlayCard.Click += BtnPlayCard_Click;
            this.Controls.Add(btnPlayCard);

            // Initialize Countdown Timer
            gameTimer = new System.Windows.Forms.Timer();
            gameTimer.Interval = 1000; // 1 second
            gameTimer.Tick += GameTimer_Tick;
            gameTimer.Start();
        }

        private void GameTimer_Tick(object sender, EventArgs e)
        {
            timeLeft--;
            lblTimer.Text = $"Time: {timeLeft}s";

            if (timeLeft <= 0)
            {
                gameTimer.Stop();
                MessageBox.Show("Time's Up! Match Over.", "DeckDuels");
                btnPlayCard.Enabled = false;
            }
        }

        private void BtnPlayCard_Click(object sender, EventArgs e)
        {
            // Adding weight when you play a card
            playerWeight += 10;
            if (playerWeight > 100) playerWeight = 100;
            pbScale.Value = playerWeight;

            if (playerWeight >= 100)
            {
                gameTimer.Stop();
                MessageBox.Show("OVERLOAD! You accumulated too much weight and lost!", "Defeat");
                btnPlayCard.Enabled = false;
            }
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DeckDuelsForm());
        }
    }
}
