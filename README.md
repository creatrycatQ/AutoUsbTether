# AutoUsbTether

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows_10%2B-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Release](https://img.shields.io/badge/Download-EXE-brightgreen)](../../releases/latest)

电脑插上手机 → 自动开启 USB 网络共享。系统托盘静默运行，XP 经典弹窗通知。

## 功能

- 📱 插入手机自动开启 USB 网络共享（10 秒延迟，等待手机就绪）
- 📥 首次运行自动下载安装 ADB（Google Platform Tools）
- 🖥️ 最小化到系统托盘，无窗口、无控制台
- 🔔 XP 风格通知弹窗（非 Win10 气泡）
- 🔄 网络共享意外关闭自动恢复
- 🚀 支持开机自启动
- ⚡ 双重方案：`svc usb` 失败自动走 `service call connectivity` 备用

## 使用方法

1. 手机开启 **USB 调试**（开发者选项 → USB 调试）
2. 下载 [AutoUsbTether.exe](../../releases/latest)
3. 双击运行 → 自动隐藏到托盘
4. 插入手机 → 10 秒后自动开启网络共享

首次运行会自动下载 ADB（约 10MB），请保持网络连接。

## 构建

```powershell
# 需要 .NET 8.0 SDK
cd AutoUsbTether
dotnet build

# 单文件发布（需 .NET 8 运行时，约 240KB）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

# 完全独立发布（无需运行时，约 70MB）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## 原理

```
USB 插入 → ADB 检测设备 → 等待 10 秒 → svc usb setFunctions rndis
                                    ↘ 失败 → service call connectivity 33
```

使用 ADB 命令切换手机 USB 模式为 RNDIS（远程网络驱动接口规范），电脑端识别为网卡。

## 项目结构

```
AutoUsbTether/
├── AutoUsbTether.csproj   # .NET 8 WinForms 项目
├── Program.cs             # 全部源码
│   ├── TrayApplicationContext  # 系统托盘 + 轮询逻辑
│   ├── AdbManager              # ADB 下载/安装/命令
│   ├── ToastForm               # XP 风格通知弹窗
│   └── AutoStart               # 注册表开机自启
└── .gitignore
```

## 系统要求

- Windows 10 / 11 (x64)
- .NET 8.0 Desktop Runtime（独立发布版无需）
- 安卓手机需开启 USB 调试
