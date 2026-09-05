# MagicBulletFullEffects

魔弹射手（MCH）简易特效 → 全部特效。

## 功能

在游戏设置为"简易特效"时，强制魔弹射手（Action ID: 29415）显示全部特效。其他技能不受影响。

## 原理

- 检测到施放魔弹射手时，临时将 BattleEffects 切换为"全部"
- 施放结束后恢复原始设置
- 每帧检测，无卡顿

## 安装

1. 在 Dalamud 设置中添加仓库：
   ```
   https://raw.githubusercontent.com/HAAAAe1/MyPlugins/main/pluginmaster.json
   ```
2. 在插件管理器中搜索 "MagicBulletFullEffects" 安装

## 兼容性

- Dalamud API Level 9+
- 需要 FFXIVClientStructs

## 注意事项

- BattleEffects 是客户端设置，修改不影响服务器
- 插件仅在施放魔弹射手时临时修改设置，其他时间保持原值
