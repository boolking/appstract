﻿#region Copyright (C) 2008-2009 Simon Allaeys

/*
    Copyright (C) 2008-2009 Simon Allaeys
 
    This file is part of AppStract

    AppStract is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    AppStract is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with AppStract.  If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Threading;
using AppStract.Core.Logging;
using AppStract.Core.Virtualization.Synchronization;
using AppStract.Server.FileSystem;
using AppStract.Server.Hooking;
using AppStract.Server.Registry;
using EasyHook;

namespace AppStract.Server
{
  /// <summary>
  /// The static core class of the guest's process.
  /// </summary>
  public static class GuestCore
  {

    #region Variables

    /// <summary>
    /// Indicates whether the <see cref="GuestCore"/> is initialized.
    /// </summary>
    private static bool _initialized;
    /// <summary>
    /// The object to lock when initializing the <see cref="GuestCore"/>.
    /// </summary>
    private static readonly object _initializationLock = new object();
    /// <summary>
    /// The process ID of the current process.
    /// </summary>
    private static int _currentProcessId;
    /// <summary>
    /// The object to use to report messages to the server and to test connectivity with.
    /// </summary>
    private static IServerReporter _serverReporter;
    /// <summary>
    /// The object responsible for communicating back-end database queries to the server.
    /// </summary>
    private static CommunicationBus _commBus;
    /// <summary>
    /// Contains all handlers for the installed API hooks.
    /// </summary>
    private static HookImplementations _hookImplementations;
    /// <summary>
    /// The maximum allowed <see cref="LogLevel"/> of <see cref="LogMessage"/>s to report.
    /// </summary>
    private static LogLevel _logLevel;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the ID of the current process.
    /// </summary>
    public static int ProcessId
    {
      get { return _currentProcessId; }
    }

    /// <summary>
    /// Gets whether the core has been initialized for the current process.
    /// </summary>
    public static bool Initialized
    {
      get { return _initialized; }
    }

    /// <summary>
    /// Gets whether the connection from the guest to the host is valid.
    /// </summary>
    public static bool ValidConnection
    {
      get
      {
        try
        {
          _serverReporter.Ping();
          return true;
        }
        catch
        {
          return false;
        }
      }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Initializes the <see cref="GuestCore"/>.
    /// </summary>
    /// <param name="processSynchronizer">
    /// The <see cref="IProcessSynchronizer"/> to use for communication with the host.
    /// </param>
    public static void Initialize(IProcessSynchronizer processSynchronizer)
    {
      lock (_initializationLock)
      {
        if (_initialized)
          return;
        _currentProcessId = RemoteHooking.GetCurrentProcessId();
        /// Attach ProcessExit event handler.
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        /// Initialize variables.
        _serverReporter = processSynchronizer;
        _logLevel = _serverReporter.GetRequiredLogLevel();
        _commBus = new CommunicationBus(processSynchronizer, processSynchronizer);
        /// Load resources.
        _serverReporter.ReportMessage(new LogMessage(LogLevel.Information,
                                                     "Process [{0}] will now load the file system and registry data.",
                                                     _currentProcessId));
        var fileSystem = new FileSystemProvider(processSynchronizer.FileSystemRoot);
        fileSystem.LoadFileTable(_commBus);
        _serverReporter.ReportMessage(new LogMessage(LogLevel.Debug,
                                                     "Process [{0}] succesfully loaded the file system.",
                                                     _currentProcessId));
        var registry = new RegistryProvider();
        registry.LoadRegistry(_commBus);
        _serverReporter.ReportMessage(new LogMessage(LogLevel.Debug,
                                                     "Process [{0}] succesfully loaded the registry.",
                                                     _currentProcessId));
        _hookImplementations = new HookImplementations(fileSystem, registry);
        _commBus.AutoFlush = true;
        _initialized = true;
        _serverReporter.ReportMessage(new LogMessage(LogLevel.Information,
                                                     "Process [{0}] succesfully initialized its core.",
                                                     _currentProcessId));
      }
    }

    /// <summary>
    /// Installs all hooks, <see cref="Initialize"/> must be called before.
    /// </summary>
    /// <exception cref="GuestException">
    /// A <see cref="GuestException"/> is thrown if <see cref="Initialize"/> hasn't been called
    /// before installing the hooks.
    /// </exception>
    /// <param name="inCallBack">The callback object for the hooks.</param>
    public static void InstallHooks(object inCallBack)
    {
      lock (_initializationLock)
      {
        if (!_initialized)
          throw new GuestException("The GuestCore must be initialized before hook installation can start.");
      }
      HookManager.Initialize(inCallBack, _hookImplementations);
      HookManager.InstallHooks();
      Log(new LogMessage(LogLevel.Information, "Process [PID{0}] is hooked.", _currentProcessId));
      Log(new LogMessage(LogLevel.Information, "Process [PID{0}] is ready to wake up.", _currentProcessId));
    }

    /// <summary>
    /// Sends the <see cref="LogMessage"/> specified to the host.
    /// </summary>
    /// <remarks>
    /// To optimize performance, there's no check whether the GuestCore has been initialized already.
    /// Internally, a <see cref="NullReferenceException"/> is caught if the core isn't initialized.
    /// </remarks>
    /// <exception cref="GuestException">
    /// A call to <see cref="Initialize"/> must be completed before using the log functionality.
    /// =OR=
    /// An unexpected <see cref="Exception"/> occured.
    /// </exception>
    /// <param name="message"></param>
    public static void Log(LogMessage message)
    {
      Log(message, true);
    }

    /// <summary>
    /// Sends the <see cref="LogMessage"/> specified to the host.
    /// </summary>
    /// <remarks>
    /// To optimize performance, there's no check whether the GuestCore has been initialized already.
    /// Internally, a <see cref="NullReferenceException"/> is caught if the core isn't initialized.
    /// </remarks>
    /// <exception cref="GuestException">
    /// A call to <see cref="Initialize"/> must be completed before using the log functionality.
    /// =OR=
    /// An unexpected <see cref="Exception"/> occured.
    /// </exception>
    /// <param name="message"></param>
    /// <param name="throwOnError"></param>
    public static void Log(LogMessage message, bool throwOnError)
    {
      if (Thread.CurrentThread.Name == null)
        Thread.CurrentThread.Name = "Guest";
      if (message.Level > _logLevel)
        return;
      try
      {
        /// Note: Queue these message, similar to CommunicationBus?
        _serverReporter.ReportMessage(message);
      }
      catch (NullReferenceException e)
      {
        if (throwOnError)
          throw new GuestException("The GuestCore must be initialized before using the log functionality.", e);
      }
      catch (Exception e)
      {
        if (throwOnError)
          throw new GuestException("An unexpected exception occured.", e);
      }
    }

    #endregion

    #region Private Methods

    private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
      _commBus.Flush();
    }

    #endregion

  }
}