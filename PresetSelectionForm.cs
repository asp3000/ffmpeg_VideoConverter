// ============================================================================
//  PresetSelectionForm.cs — choose a preset from the UniConverter database.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class PresetSelectionForm : Form
    {
        private string _selectedCategory;
        private FlowLayoutPanel _listPanel;
        private List<Button> _tabButtons = new List<Button>();
        private TextBox _searchBox;

        /// <summary>Chosen preset (read after DialogResult.OK).</summary>
        public PresetOption SelectedPreset { get; private set; }

        public PresetSelectionForm()
        {
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "选择预设";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(720, 520);

            BuildTabs();
            BuildSearch();
            BuildList();

            // Default to "最近" if present, otherwise first category.
            string first = PresetDataStore.Categories.FirstOrDefault();
            if (PresetDataStore.Categories.Contains("最近"))
                first = "最近";
            else if (string.IsNullOrEmpty(first))
                first = "常用";
            SelectCategory(first);
        }

        private void BuildTabs()
        {
            var tabPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Color.White,
                Padding = new Padding(12, 8, 12, 0)
            };
            this.Controls.Add(tabPanel);

            var cats = new List<string>(PresetDataStore.Categories);
            if (cats.Count == 0)
                cats.Add("常用");

            int x = 12;
            foreach (var cat in cats)
            {
                var btn = new Button
                {
                    Text = cat,
                    Location = new Point(x, 8),
                    Size = new Size(TextRenderer.MeasureText(cat, this.Font).Width + 24, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(80, 80, 80),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    Tag = cat
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += TabButton_Click;
                tabPanel.Controls.Add(btn);
                _tabButtons.Add(btn);
                x += btn.Width + 4;
            }
        }

        private void BuildSearch()
        {
            var searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.White,
                Padding = new Padding(12, 4, 12, 4)
            };
            this.Controls.Add(searchPanel);

            _searchBox = new TextBox
            {
                Location = new Point(searchPanel.Width - 180 - 12, 6),
                Size = new Size(180, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.Gray
            };
            _searchBox.Text = "搜索";
            _searchBox.GotFocus += (s, e) =>
            {
                if (_searchBox.Text == "搜索")
                {
                    _searchBox.Text = "";
                    _searchBox.ForeColor = Color.Black;
                }
            };
            _searchBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_searchBox.Text))
                {
                    _searchBox.Text = "搜索";
                    _searchBox.ForeColor = Color.Gray;
                }
            };
            _searchBox.TextChanged += (s, e) => RefreshList();
            searchPanel.Controls.Add(_searchBox);
        }

        private void BuildList()
        {
            _listPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 246, 252),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 12, 12, 12)
            };
            this.Controls.Add(_listPanel);
            _listPanel.BringToFront();
        }

        private void TabButton_Click(object sender, EventArgs e)
        {
            SelectCategory((string)((Button)sender).Tag);
        }

        private void SelectCategory(string category)
        {
            _selectedCategory = category;
            foreach (var btn in _tabButtons)
            {
                bool active = (string)btn.Tag == category;
                btn.BackColor = active ? Color.FromArgb(124, 77, 255) : Color.White;
                btn.ForeColor = active ? Color.White : Color.FromArgb(80, 80, 80);
                btn.Font = new Font("Microsoft YaHei UI", 9F, active ? FontStyle.Bold : FontStyle.Regular);
            }
            RefreshList();
        }

        private void RefreshList()
        {
            _listPanel.Controls.Clear();

            string keyword = _searchBox != null && _searchBox.Text != "搜索" ? _searchBox.Text.Trim() : "";

            IEnumerable<PresetRowSource> source;
            if (_selectedCategory == "最近")
            {
                source = PresetDataStore.RecentPresets.Select(p => new PresetRowSource
                {
                    FormatTitle = p.FormatName,
                    Preset = p
                });
            }
            else if (_selectedCategory == "常用" || !PresetDataStore.FormatsByCategory.ContainsKey(_selectedCategory))
            {
                // Fallback: built-in presets.
                source = PresetOption.BuiltInAll.Select(p => new PresetRowSource
                {
                    FormatTitle = p.FormatName,
                    Preset = p
                });
            }
            else
            {
                source = PresetDataStore.FormatsByCategory[_selectedCategory]
                    .SelectMany(f => f.Presets, (f, p) => new PresetRowSource
                    {
                        FormatTitle = f.Title,
                        Preset = p
                    });
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string k = keyword.ToLowerInvariant();
                source = source.Where(r =>
                    (r.FormatTitle ?? "").ToLowerInvariant().Contains(k) ||
                    (r.Preset.Name ?? "").ToLowerInvariant().Contains(k) ||
                    (r.Preset.ResolutionLabel ?? "").ToLowerInvariant().Contains(k));
            }

            foreach (var row in source)
            {
                var panel = BuildPresetRow(row.FormatTitle, row.Preset);
                _listPanel.Controls.Add(panel);
            }
        }

        private Panel BuildPresetRow(string formatTitle, PresetOption preset)
        {
            int rowW = Math.Max(660, _listPanel.ClientSize.Width - 40);
            int rowH = 48;

            var panel = new Panel
            {
                Width = rowW,
                Height = rowH,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };

            // Format icon placeholder (colored rounded square with format initial).
            var icon = new Label
            {
                Location = new Point(12, 10),
                Size = new Size(28, 28),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                Text = string.IsNullOrEmpty(formatTitle) ? "?" : formatTitle.Substring(0, 1).ToUpperInvariant(),
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            DrawRounded(icon, 6);
            panel.Controls.Add(icon);

            // Format name.
            panel.Controls.Add(new Label
            {
                Location = new Point(50, 8),
                Size = new Size(80, 18),
                Text = formatTitle,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent
            });

            // Preset name.
            panel.Controls.Add(new Label
            {
                Location = new Point(140, 8),
                Size = new Size(160, 18),
                Text = preset.Name,
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = Color.FromArgb(60, 60, 60),
                BackColor = Color.Transparent
            });

            // Resolution.
            panel.Controls.Add(new Label
            {
                Location = new Point(310, 8),
                Size = new Size(140, 18),
                Text = preset.ResolutionLabel ?? "自动",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(120, 120, 120),
                BackColor = Color.Transparent
            });

            // Delete button (visual only in this dialog; does not mutate store).
            var btnDelete = new Button
            {
                Location = new Point(rowW - 44, 10),
                Size = new Size(28, 28),
                Text = "🗑",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(180, 80, 80),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btnDelete.FlatAppearance.BorderSize = 0;
            btnDelete.Click += (s, e) =>
            {
                _listPanel.Controls.Remove(panel);
                panel.Dispose();
            };
            panel.Controls.Add(btnDelete);

            // Hover + click selection.
            panel.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(240, 235, 250);
            panel.MouseLeave += (s, e) => panel.BackColor = Color.White;
            foreach (Control c in panel.Controls)
            {
                if (c == btnDelete) continue;
                c.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(240, 235, 250);
                c.MouseLeave += (s, e) => panel.BackColor = Color.White;
                c.Click += (s, e) => SelectPreset(preset);
            }
            panel.Click += (s, e) => SelectPreset(preset);

            return panel;
        }

        private void SelectPreset(PresetOption preset)
        {
            SelectedPreset = preset.Clone();
            PresetDataStore.AddRecent(preset);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void DrawRounded(Label label, int radius)
        {
            label.Paint += (s, e) =>
            {
                using (var path = new GraphicsPath())
                {
                    int r = radius;
                    int w = label.Width - 1;
                    int h = label.Height - 1;
                    path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                    path.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
                    path.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    label.Region = new Region(path);
                }
            };
        }

        private class PresetRowSource
        {
            public string FormatTitle { get; set; }
            public PresetOption Preset { get; set; }
        }
    }
}
