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
                Height = 46,
                BackColor = Color.White,
                Padding = new Padding(12, 10, 12, 0)
            };
            this.Controls.Add(tabPanel);

            var tabs = new List<string> { "最近使用" };
            tabs.AddRange(PresetDataStore.Categories);
            tabs.Add("自定义");

            int x = 12;
            foreach (var tab in tabs)
            {
                var btn = new Button
                {
                    Text = tab,
                    Location = new Point(x, 8),
                    Size = new Size(TextRenderer.MeasureText(tab, this.Font).Width + 22, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(80, 80, 80),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    Tag = tab
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += TabButton_Click;
                tabPanel.Controls.Add(btn);
                _tabButtons.Add(btn);
                x += btn.Width + 4;
            }
        }

        private void TabButton_Click(object sender, EventArgs e)
        {
            SelectTab((string)((Button)sender).Tag);
        }

        private void SelectTab(string tab)
        {
            _selectedTab = tab;
            foreach (var btn in _tabButtons)
            {
                bool active = (string)btn.Tag == tab;
                btn.BackColor = active ? Color.FromArgb(124, 77, 255) : Color.White;
                btn.ForeColor = active ? Color.White : Color.FromArgb(80, 80, 80);
                btn.Font = new Font("Microsoft YaHei UI", 9F, active ? FontStyle.Bold : FontStyle.Regular);
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
            // 单列列表（最近使用 / 自定义）
            _singleList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 246, 252),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(14, 14, 14, 14)
            };
            this.Controls.Add(_singleList);

            // 右侧预设列表（先加，Dock Fill）
            _splitRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 246, 252),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(14, 14, 14, 14)
            };
            this.Controls.Add(_splitRight);

            // 左侧格式列表（后加，Dock Left 预留宽度）
            _splitLeft = new Panel
            {
                Dock = DockStyle.Left,
                Width = 172,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true,
                Padding = new Padding(6, 6, 6, 6)
            };
            this.Controls.Add(_splitLeft);
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

            int y = 6;
            foreach (var fmt in formats)
            {
                var btn = new Button
                {
                    Location = new Point(6, y),
                    Size = new Size(150, 30),
                    Text = fmt.Title,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(70, 70, 70),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0),
                    Tag = fmt.FormatId
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) => SelectFormat((string)btn.Tag, btn);
                _splitLeft.Controls.Add(btn);
                y += 36;
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
            int usable = showCategory
                ? Math.Max(600, this.ClientSize.Width - 60)
                : Math.Max(340, this.ClientSize.Width - 172 - 60);
            int rowW = usable;
            int rowH = 44;

            var panel = new Panel
            {
                Width = rowW,
                Height = rowH,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };

            int x = 12;
            if (showCategory)
            {
                var chip = new Label
                {
                    Location = new Point(x, 12),
                    Size = new Size(64, 20),
                    Text = p.Category ?? "",
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
                Location = new Point(x, 12),
                Size = new Size(220, 20),
                Text = p.Name ?? "",
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent
            });
            x += 230;

            panel.Controls.Add(new Label
            {
                Location = new Point(x, 12),
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
                    Location = new Point(rowW - 40, 8),
                    Size = new Size(28, 28),
                    Text = "🗑",
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(180, 80, 80),
                    Font = new Font("Microsoft YaHei UI", 9F)
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
                Size = new Size(Math.Max(400, this.ClientSize.Width - 60), 30),
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = Color.FromArgb(150, 150, 150),
                BackColor = Color.Transparent
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
    }
}
