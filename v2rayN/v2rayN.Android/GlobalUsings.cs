// Android namespaces are NOT imported globally (the csproj also removes the
// SDK's implicit Android.App / Android.Widget usings): many names clash with
// Avalonia (Application, Button, Color...). Platform files import them per file.
global using System;
global using System.Collections.Generic;
global using System.Collections.ObjectModel;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Controls.ApplicationLifetimes;
global using Avalonia.Input;
global using Avalonia.Interactivity;
global using Avalonia.Markup.Xaml;
global using Avalonia.Media;
global using Avalonia.Threading;
global using ReactiveUI;
global using ReactiveUI.Avalonia;
global using ReactiveUI.Primitives;
global using ReactiveUI.Primitives.Disposables;
global using ReactiveUI.Primitives.Extensions;
global using ServiceLib;
global using ServiceLib.Base;
global using ServiceLib.Common;
global using ServiceLib.Enums;
global using ServiceLib.Events;
global using ServiceLib.Handler;
global using ServiceLib.Manager;
global using ServiceLib.Models.Configs;
global using ServiceLib.Models.CoreConfigs;
global using ServiceLib.Models.Dto;
global using ServiceLib.Models.Entities;
global using ServiceLib.Resx;
global using ServiceLib.ViewModels;
