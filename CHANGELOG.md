# Changelog

## 1.0.4 — 2026-09-21

- 修复 NVIDIA Share 透明悬浮层导致桌面 F1/F2 快捷键失效的问题。
- 自动分类保留已有 Fence 归属，避免手动拖入的文件再次跳到其他分组。
- 文件操作成功后不再显示常驻完成提示，快速操作不显示进度面板。
- 修复进度和异常提示面板的窗口裁剪及点击区域。
- 文件移动和桌面内容更新时复用 Fence 控件，并保留列表滚动位置。
- 清理废弃的注释诊断代码。保留回归测试和诊断工具。

Includes the desktop shortcut, manual-drop ownership, transfer notification,
and scroll-position fixes verified in the 1.0.4 test builds.
