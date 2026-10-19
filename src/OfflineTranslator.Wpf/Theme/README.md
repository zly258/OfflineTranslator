# Theme 使用说明

## 1. 文件职责

| 文件 | 内容 |
|---|---|
| `Tokens.xaml` | `Color.*`、`Brush.*`、`Font*`、`Layout.*`、`Radius.*`、`Text.*`、`Icon.Size.*` |
| `Controls.xaml` | Window、TextBlock/AccessText、Button、TextBox、PasswordBox、ComboBox、CheckBox、RadioButton、GroupBox、Expander、Tab、ToolTip、ProgressBar、StatusBar、ScrollBar、GridSplitter、Menu |
| `DataControls.xaml` | ListBox、ListView、DataGrid、TreeView、ScrollViewer.Base |
| `Icons.xaml` | 24x24 矢量几何图形 (`Icon.Geo.*`)、基础与尺寸样式 (`Icon.*`)、图标按钮 (`Button.Icon*`) |
| `Theme.xaml` | 唯一推荐入口，合并 Controls + DataControls + Icons |

`Controls.xaml`、`DataControls.xaml` 和 `Icons.xaml` 各自合并了 `Tokens.xaml`，
所以 `Theme.xaml` 不再重复合并 Tokens。

---

## 2. App.xaml 接入

```xml
<Application x:Class="YourApp.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary
                    Source="pack://application:,,,/YourAssembly;component/Theme/Theme.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`YourAssembly` 是包含 Theme 文件的程序集名。

---

## 3. 插件项目接入

AutoCAD / Inventor 插件通常没有 `App.xaml`。在窗口基类或 ThemeLoader 中合并：

```csharp
using System;
using System.Windows;

namespace YourPlugin.UI
{
    public static class ThemeLoader
    {
        private static bool _applied;

        public static void Apply(Window window)
        {
            if (window == null) return;

            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/YourPluginAssembly;component/Theme/Theme.xaml",
                        UriKind.Absolute)
                });
            }
            catch (Exception)
            {
                // 主题加载失败不应阻断业务窗口
            }
        }
    }
}
```

在每个窗口构造函数中：

```csharp
public SettingsWindow()
{
    ThemeLoader.Apply(this);
    InitializeComponent();
}
```

> 如果主题放在共享库中，把 `YourPluginAssembly` 换成共享库程序集名，
> 例如 `OpenCADTranslate.UI`。

---

## 4. 资源顺序

如果手动合并（不推荐）：

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Tokens.xaml" />
    <ResourceDictionary Source="Controls.xaml" />
    <ResourceDictionary Source="DataControls.xaml" />
</ResourceDictionary.MergedDictionaries>
```

顺序必须是：**Tokens → Controls → DataControls**。

如果使用 `Theme.xaml`，内部已经处理好。

---

## 5. 使用变体

```xml
<Button Content="保存" Style="{StaticResource Button.Primary}" />
<Button Content="删除" Style="{StaticResource Button.Danger}" />
<Button Content="更多" Style="{StaticResource Button.Text}" />
<Button Content="小按钮" Style="{StaticResource Button.Primary.Small}" />

<TextBox Text="{Binding Name}" />
<TextBox Text="{Binding Log}" Style="{StaticResource TextBox.Multiline}" />
<TextBox Text="{Binding Json}" Style="{StaticResource TextBox.Code}" />
```

隐式样式（不需要指定）：

- `Window`
- `TextBlock`
- `AccessText`
- `Button`（默认 Secondary）
- `TextBox`
- `PasswordBox`
- `ComboBox`
- `ComboBoxItem`
- `CheckBox`
- `RadioButton`
- `GroupBox`
- `Expander`
- `TabControl`
- `TabItem`
- `ToolTip`
- `ProgressBar`
- `ScrollBar`
- `StatusBar`
- `StatusBarItem`
- `ListBox`
- `ListBoxItem`
- `ListView`
- `ListViewItem`
- `DataGrid` / `DataGridRow` / `DataGridCell` / `DataGridColumnHeader`
- `TreeView` / `TreeViewItem`

---

## 6. 扩展主题

### 新增颜色

1. 在 `Tokens.xaml` 增加 `Color.*` 和 `Brush.*`。
2. 在 `docs/01-design-tokens.md` 登记。
3. 在 `tools/ContrastCheck.ps1` 增加对比度组合。
4. 运行脚本。

### 新增控件样式

1. 优先在 `Controls.xaml` / `DataControls.xaml` 中扩展，不要在每个窗口复制。
2. 必须覆盖 Normal / Hover / Pressed / Focused / Selected / Selected+Hover / Disabled。
3. 每个背景变化必须配前景变化。
4. 使用 `Brush.*`，不要写死颜色。

---

## 7. 兼容性

- .NET Framework 4.8：支持。
- .NET 8+：支持。
- 不使用第三方控件库。
- 不使用 Windows 11 专属 API。
- `TextOptions.TextFormattingMode="Display"` 在 Window 样式中设置；
  不要在每个子元素上重复设置。
- `UseLayoutRounding` / `SnapsToDevicePixels` 在 Window 样式中设置。

---

## 8. 常见问题

### 主题不生效

- 检查 `pack://` 程序集名。
- 检查 `Theme.xaml` 是否被复制到输出目录。
- 检查窗口是否在 `InitializeComponent` 之前合并了资源。
- 检查是否有窗口级资源字典覆盖了隐式样式。

### 某些控件没有样式

- 隐式样式只对 `TargetType` 精确匹配；子类控件需要单独定义。
- 第三方控件不会自动继承基础控件样式。
- 检查是否被局部 `Style` 覆盖。

### 字体发虚

- 确认 Window 根设置了 `TextFormattingMode="Display"`。
- 确认没有局部 `FontFamily`。
- 确认没有非整数 `FontSize`（本规范使用 13.5，可接受；不要用 13.37）。

### 主按钮文字变深

- 检查隐式 `TextBlock` 是否硬编码了 `Foreground`。
- 检查 `ContentPresenter` 是否传递 `TextElement.Foreground`。
- 运行 `tests/ThemeContractTests.cs`。
