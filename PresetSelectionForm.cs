// ============================================================================
//  PresetSelectionForm.cs — choose a preset with a two-level tab interface.
//
//  顶部页签：最近使用 | 视频 | 音频 | 图像 | 设备 | 网络视频 | 自定义
//   * 最近使用 / 自定义：单列列表（类别 / 名称 / 分辨率），带删除图标
//   * 视频 / 音频 / 图像 / 设备 / 网络视频：左侧可滚动格式列表（MP4…），
//     点击某格式后右侧显示该格式下的预设（名称 / 分辨率），无删除图标
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
        private string _selectedTab;
        private string _selectedFormatId;
        private string _keyword = "";

        private FlowLayoutPanel _singleList;   // 最近使用 / 自定义
        private Panel _splitLeft;              // 格式列表（可滚动）
        private FlowLayoutPanel _splitRight;    // 预设列表
        private List<Label> _tabLabels = new List<Label>();
        private Panel _tabUnderline;
        private TextBox _searchBox;
        private Panel _contentPanel;

        /// <summary>Chosen preset (read after DialogResult.OK).</summary>
        public PresetOption SelectedPreset { get; private set; }

        public PresetSelectionForm()
        {
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "选择预设";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.AutoScaleMode = AutoScaleMode.None;   // 代码构建窗体需关闭字体自动缩放，否则控件会被缩放/裁剪
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(720, 560);

            BuildTabs();
            BuildSearch();
            BuildBody();

            // 默认进入“最近使用”。
            SelectTab("最近使用");
        }

        // ---------------------------------------------------------------- //
        //  Tabs
        // ---------------------------------------------------------------- //
        private void BuildTabs()
        {
            var tabPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Color.White
            };
            this.Controls.Add(tabPanel);

            var tabs = new List<string> { "最近使用" };
            tabs.AddRange(PresetDataStore.Categories);
            tabs.Add("自定义");

            int x = 16;
            foreach (var tab in tabs)
            {
                var lbl = new Label
                {
                    Text = tab,
                    Location = new Point(x, 10),
                    AutoSize = true,
                    Cursor = Cursors.Hand,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(80, 80, 80),
                    Font = new Font("Microsoft YaHei UI", 9.75F),
                    Tag = tab
                };
                lbl.Click += TabLabel_Click;
                tabPanel.Controls.Add(lbl);
                _tabLabels.Add(lbl);
                x += lbl.Width + 22;
            }

            _tabUnderline = new Panel
            {
                Height = 3,
                BackColor = Color.FromArgb(124, 77, 255),
                Visible = false
            };
            tabPanel.Controls.Add(_tabUnderline);
        }

        private void TabLabel_Click(object sender, EventArgs e)
        {
            SelectTab((string)((Label)sender).Tag);
        }

        private void SelectTab(string tab)
        {
            _selectedTab = tab;
            Label activeLabel = null;
            foreach (var lbl in _tabLabels)
            {
                bool active = (string)lbl.Tag == tab;
                lbl.ForeColor = active ? Color.FromArgb(124, 77, 255) : Color.FromArgb(80, 80, 80);
                lbl.Font = new Font("Microsoft YaHei UI", 9.75F, active ? FontStyle.Bold : FontStyle.Regular);
                if (active) activeLabel = lbl;
            }

            if (activeLabel != null)
            {
                _tabUnderline.Width = activeLabel.Width;
                _tabUnderline.Location = new Point(activeLabel.Left, activeLabel.Bottom + 4);
                _tabUnderline.Visible = true;
            }

            bool split = tab != "最近使用" && tab != "自定义";
            _singleList.Visible = !split;
            _splitLeft.Visible = split;
            _splitRight.Visible = split;

            if (tab == "最近使用") BuildRecentList();
            else if (tab == "自定义") BuildCustomList();
            else BuildSplitLeft(tab);
        }

        // ---------------------------------------------------------------- //
        //  Search
        // ---------------------------------------------------------------- //
        private void BuildSearch()
        {
            var searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.White
            };
            this.Controls.Add(searchPanel);

            _searchBox = new TextBox
            {
                Location = new Point(searchPanel.Width - 180 - 16, 8),
                Size = new Size(180, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.Gray
            };
            _searchBox.Text = "搜索";
            _searchBox.GotFocus += (s, e) =>
            {
                if (_searchBox.Text == "搜索") { _searchBox.Text = ""; _searchBox.ForeColor = Color.Black; }
            };
            _searchBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_searchBox.Text)) { _searchBox.Text = "搜索"; _searchBox.ForeColor = Color.Gray; }
            };
            _searchBox.TextChanged += (s, e) =>
            {
                _keyword = _searchBox.Text == "搜索" ? "" : _searchBox.Text.Trim().ToLowerInvariant();
                RefreshCurrent();
            };
            searchPanel.Controls.Add(_searchBox);
        }

        // ---------------------------------------------------------------- //
        //  Body
        // ---------------------------------------------------------------- //
        private void BuildBody()
        {
            _contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(248, 246, 252),
                Padding = new Padding(0)
            };
            // 关键：必须置于最底层（z-order 后端），否则 Dock Fill 会在 Top 面板
            // 之前被布局，撑满整个窗口高度，导致分类/预设列表首项被顶部页签遮挡。
            this.Controls.Add(_contentPanel);
            this.Controls.SetChildIndex(_contentPanel, 0);

            // 单列列表（最近使用 / 自定义）
            _singleList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 246, 252),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(16, 20, 16, 16)
            };
            _contentPanel.Controls.Add(_singleList);

            // 右侧预设列表（Dock Fill）
            _splitRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 246, 252),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(16, 16, 16, 16)
            };
            _contentPanel.Controls.Add(_splitRight);

            // 左侧格式列表（Dock Left）
            _splitLeft = new Panel
            {
                Dock = DockStyle.Left,
                Width = 172,
                BackColor = Color.White,
                AutoScroll = true,
                Padding = new Padding(8, 12, 8, 12)
            };
            _contentPanel.Controls.Add(_splitLeft);
            // 右侧预设列表(Dock Fill)置于底层，左侧格式列表(Dock Left)在前，
            // 保证左侧 172px 被预留、右侧填充剩余宽度。
            _contentPanel.Controls.SetChildIndex(_splitRight, 0);
        }

        private void RefreshCurrent()
        {
            if (_selectedTab == "最近使用") BuildRecentList();
            else if (_selectedTab == "自定义") BuildCustomList();
            else BuildSplitRight(_selectedFormatId);
        }

        // ---------------------------------------------------------------- //
        //  最近使用 / 自定义（单列 + 删除图标）
        // ---------------------------------------------------------------- //
        private void BuildRecentList()
        {
            _singleList.Controls.Clear();
            var recent = PresetDataStore.RecentPresets.Take(5).ToList();
            if (recent.Count == 0) { _singleList.Controls.Add(MakeEmptyLabel("暂无最近使用的预设")); return; }
            foreach (var p in recent)
            {
                if (!MatchKeyword(p)) continue;
                _singleList.Controls.Add(BuildPresetRow(p, true, true));
            }
        }

        private void BuildCustomList()
        {
            _singleList.Controls.Clear();
            if (PresetDataStore.CustomPresets.Count == 0)
            {
                _singleList.Controls.Add(MakeEmptyLabel("暂无自定义预设，可在编辑预设后保存为自定义"));
                return;
            }
            foreach (var p in PresetDataStore.CustomPresets)
            {
                if (!MatchKeyword(p)) continue;
                _singleList.Controls.Add(BuildPresetRow(p, true, true));
            }
        }

        // ---------------------------------------------------------------- //
        //  分类页签（左格式列表 + 右预设列表）
        // ---------------------------------------------------------------- //
        private void BuildSplitLeft(string category)
        {
            _splitLeft.Controls.Clear();
            if (!PresetDataStore.FormatsByCategory.ContainsKey(category)) return;
            var formats = PresetDataStore.FormatsByCategory[category];

            int y = 12;
            foreach (var fmt in formats)
            {
                var btn = new Button
                {
                    Location = new Point(8, y),
                    Size = new Size(144, 40),
                    Text = "   " + fmt.Title,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(70, 70, 70),
                    Font = new Font("Microsoft YaHei UI", 9.5F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(32, 0, 0, 0),
                    Tag = fmt.FormatId,
                    ImageAlign = ContentAlignment.MiddleLeft
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Paint += (s, e) => DrawFormatIcon(e.Graphics, fmt.Title, btn);
                btn.Click += (s, e) => SelectFormat((string)btn.Tag, btn);
                _splitLeft.Controls.Add(btn);
                y += 44;
            }

            if (formats.Count > 0)
                SelectFormat(formats[0].FormatId, _splitLeft.Controls[0] as Button);
        }

        private void SelectFormat(string formatId, Button btn)
        {
            _selectedFormatId = formatId;
            foreach (Button b in _splitLeft.Controls)
                b.BackColor = Color.White;
            if (btn != null) btn.BackColor = Color.FromArgb(237, 232, 248);
            BuildSplitRight(formatId);
        }

        private void BuildSplitRight(string formatId)
        {
            _splitRight.Controls.Clear();
            if (string.IsNullOrEmpty(_selectedTab)) return;
            var list = PresetDataStore.FormatsByCategory.ContainsKey(_selectedTab)
                ? PresetDataStore.FormatsByCategory[_selectedTab] : null;
            var fmt = list?.FirstOrDefault(f => f.FormatId == formatId);
            if (fmt == null) return;
            foreach (var p in fmt.Presets)
            {
                if (!MatchKeyword(p)) continue;
                _splitRight.Controls.Add(BuildPresetRow(p, false, false));
            }
        }

        // ---------------------------------------------------------------- //
        //  Row rendering
        // ---------------------------------------------------------------- //
        private Panel BuildPresetRow(PresetOption p, bool showCategory, bool showDelete)
        {
            int rowW = showCategory
                ? Math.Max(600, _contentPanel.ClientSize.Width - 48)
                : Math.Max(340, _contentPanel.ClientSize.Width - _splitLeft.Width - 48);
            int rowH = 48;

            var panel = new Panel
            {
                Width = rowW,
                Height = rowH,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };

            int x = 12;

            // 小图标
            var iconBox = new PictureBox
            {
                Location = new Point(x, 14),
                Size = new Size(20, 20),
                BackColor = Color.Transparent
            };
            iconBox.Paint += (s, e) => DrawPresetIcon(e.Graphics, p.Category, iconBox.ClientRectangle);
            panel.Controls.Add(iconBox);
            x += 28;

            if (showCategory)
            {
                // 显示格式类型（如 MP4），而非大类（视频/音频），便于区分最近使用项。
                string typeText = !string.IsNullOrEmpty(p.FormatName) ? p.FormatName
                    : (!string.IsNullOrEmpty(p.FormatId) ? p.FormatId.ToUpperInvariant() : (p.Category ?? ""));
                var chip = new Label
                {
                    Location = new Point(x, 13),
                    Size = new Size(64, 22),
                    Text = typeText,
                    BackColor = Color.FromArgb(237, 232, 248),
                    ForeColor = Color.FromArgb(90, 60, 160),
                    Font = new Font("Microsoft YaHei UI", 8.5F),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                DrawRounded(chip, 5);
                panel.Controls.Add(chip);
                x += 76;
            }

            panel.Controls.Add(new Label
            {
                Location = new Point(x, 14),
                Size = new Size(220, 20),
                Text = p.Name ?? "",
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent
            });
            x += 230;

            panel.Controls.Add(new Label
            {
                Location = new Point(x, 14),
                Size = new Size(170, 20),
                Text = p.ResolutionLabel ?? "自动",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(110, 110, 110),
                BackColor = Color.Transparent
            });

            if (showDelete)
            {
                var btnDel = new Button
                {
                    Location = new Point(rowW - 42, 10),
                    Size = new Size(28, 28),
                    Text = "🗑",
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(180, 80, 80),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    Cursor = Cursors.Hand
                };
                btnDel.FlatAppearance.BorderSize = 0;
                btnDel.Click += (s, e) => OnDelete(p);
                panel.Controls.Add(btnDel);
            }

            panel.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(240, 235, 250);
            panel.MouseLeave += (s, e) => panel.BackColor = Color.White;
            panel.Click += (s, e) => SelectPreset(p);
            foreach (Control c in panel.Controls)
            {
                if (c is Button) continue;
                c.MouseEnter += (s, e) => panel.BackColor = Color.FromArgb(240, 235, 250);
                c.MouseLeave += (s, e) => panel.BackColor = Color.White;
                c.Click += (s, e) => SelectPreset(p);
            }
            return panel;
        }

        private Label MakeEmptyLabel(string text)
        {
            return new Label
            {
                Text = text,
                Size = new Size(Math.Max(400, _contentPanel.ClientSize.Width - 60), 30),
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = Color.FromArgb(150, 150, 150),
                BackColor = Color.Transparent,
                Margin = new Padding(12, 20, 12, 0)
            };
        }

        private bool MatchKeyword(PresetOption p)
        {
            if (string.IsNullOrEmpty(_keyword)) return true;
            return (p.Name ?? "").ToLowerInvariant().Contains(_keyword)
                || (p.ResolutionLabel ?? "").ToLowerInvariant().Contains(_keyword)
                || (p.Category ?? "").ToLowerInvariant().Contains(_keyword);
        }

        // ---------------------------------------------------------------- //
        //  Selection / deletion
        // ---------------------------------------------------------------- //
        private void SelectPreset(PresetOption p)
        {
            SelectedPreset = p.Clone();
            PresetDataStore.AddRecent(p);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void OnDelete(PresetOption p)
        {
            if (_selectedTab == "最近使用")
            {
                PresetDataStore.RecentPresets.RemoveAll(x =>
                    x.Name == p.Name && x.Category == p.Category && x.FormatId == p.FormatId);
                PresetDataStore.SaveRecent();
            }
            else if (_selectedTab == "自定义")
            {
                PresetDataStore.RemoveCustom(p);
            }
            RefreshCurrent();
        }

        // ---------------------------------------------------------------- //
        //  Drawing helpers
        // ---------------------------------------------------------------- //
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

        private void DrawFormatIcon(Graphics g, string formatTitle, Button host)
        {
            var bounds = new Rectangle(8, 10, 20, 20);
            var (back, fore) = FormatColor(formatTitle);
            using (var path = RoundedRect(bounds, 5))
            {
                using (var brush = new SolidBrush(back))
                    g.FillPath(brush, path);
            }
            string text = GetFormatAbbreviation(formatTitle);
            using (var brush = new SolidBrush(fore))
            using (var font = new Font("Microsoft YaHei UI", 7F, FontStyle.Bold))
            {
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, brush,
                    bounds.X + (bounds.Width - size.Width) / 2,
                    bounds.Y + (bounds.Height - size.Height) / 2);
            }
        }

        private void DrawPresetIcon(Graphics g, string category, Rectangle bounds)
        {
            var (back, fore) = CategoryColor(category);
            using (var path = RoundedRect(bounds, 5))
            {
                using (var brush = new SolidBrush(back))
                    g.FillPath(brush, path);
            }
            using (var brush = new SolidBrush(fore))
            using (var font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold))
            {
                var size = g.MeasureString("▶", font);
                g.DrawString("▶", font, brush,
                    bounds.X + (bounds.Width - size.Width) / 2,
                    bounds.Y + (bounds.Height - size.Height) / 2);
            }
        }

        private GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int r = radius;
            int w = rect.Width - 1;
            int h = rect.Height - 1;
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.X + w - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.X + w - r * 2, rect.Y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        private string GetFormatAbbreviation(string title)
        {
            if (string.IsNullOrEmpty(title)) return "?";
            var upper = title.ToUpperInvariant();
            if (upper.Contains("MP4")) return "MP4";
            if (upper.Contains("MKV")) return "MKV";
            if (upper.Contains("MOV")) return "MOV";
            if (upper.Contains("AVI")) return "AVI";
            if (upper.Contains("HEVC") || upper.Contains("H.265")) return "HVC";
            if (upper.Contains("AVC") || upper.Contains("H.264")) return "AVC";
            if (upper.Contains("PRORES")) return "PRO";
            if (upper.Contains("CINEFORM")) return "CIN";
            if (title.Length >= 3) return title.Substring(0, 3).ToUpperInvariant();
            return title.ToUpperInvariant();
        }

        private (Color back, Color fore) FormatColor(string title)
        {
            var upper = (title ?? "").ToUpperInvariant();
            if (upper.Contains("MP4")) return (Color.FromArgb(124, 77, 255), Color.White);
            if (upper.Contains("MKV")) return (Color.FromArgb(77, 124, 255), Color.White);
            if (upper.Contains("MOV")) return (Color.FromArgb(255, 124, 77), Color.White);
            if (upper.Contains("AVI")) return (Color.FromArgb(77, 200, 170), Color.White);
            if (upper.Contains("HEVC")) return (Color.FromArgb(100, 80, 200), Color.White);
            if (upper.Contains("PRORES")) return (Color.FromArgb(220, 100, 150), Color.White);
            return (Color.FromArgb(160, 140, 220), Color.White);
        }

        private (Color back, Color fore) CategoryColor(string category)
        {
            var c = category ?? "";
            if (c.Contains("视频")) return (Color.FromArgb(230, 220, 255), Color.FromArgb(124, 77, 255));
            if (c.Contains("音频")) return (Color.FromArgb(220, 235, 255), Color.FromArgb(77, 124, 255));
            if (c.Contains("图像")) return (Color.FromArgb(255, 230, 220), Color.FromArgb(255, 124, 77));
            if (c.Contains("设备")) return (Color.FromArgb(220, 255, 235), Color.FromArgb(60, 180, 120));
            if (c.Contains("网络")) return (Color.FromArgb(255, 240, 220), Color.FromArgb(220, 150, 50));
            return (Color.FromArgb(237, 232, 248), Color.FromArgb(124, 77, 255));
        }
    }
}
