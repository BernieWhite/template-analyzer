﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Azure.Templates.Analyzer.Types;
using Microsoft.Extensions.Logging;

using Powershell = System.Management.Automation.PowerShell; // There's a conflict between this class name and a namespace

namespace Microsoft.Azure.Templates.Analyzer.RuleEngines.PowerShellEngine
{
    /// <summary>
    /// Executes template analysis encoded in PowerShell.
    /// </summary>
    public class PowerShellRuleEngine : IRuleEngine
    {
        /// <summary>
        /// Execution environment for PowerShell.
        /// </summary>
        private readonly Runspace runspace;

        /// <summary>
        /// Logger for logging notable events.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// Regex that matches a string like: " on line: aNumber".
        /// </summary>
        private readonly Regex lineNumberRegex = new(@"\son\sline:\s\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Creates a new instance of a PowerShellRuleEngine.
        /// </summary>
        /// <param name="logger">A logger to report errors and debug information.</param>
        public PowerShellRuleEngine(ILogger logger = null)
        {
            this.logger = logger;

            try
            {
                // There are 2 different 'Default' functions available:
                // https://docs.microsoft.com/powershell/scripting/developer/hosting/creating-an-initialsessionstate?view=powershell-7.2
                // CreateDefault2 appears to not have a dependency on Microsoft.Management.Infrastructure.dll,
                // which is missing when publishing for 'win-x64', and PowerShell throws an exception creating the InitialSessionState.
                // Notably, Microsoft.Management.Infrastructure.dll is available when publishing for specific Windows versions (such as win7-x64),
                // but since this libary is not needed here, might as well just eliminate the dependency.
                var initialState = InitialSessionState.CreateDefault2();

                if (Platform.IsWindows)
                {
                    // Ensure we can execute the signed bundled scripts.
                    // (This sets the policy at the Process scope.)
                    // When custom PS rules are supported, we may need to update this to be more relaxed.
                    initialState.ExecutionPolicy = PowerShell.ExecutionPolicy.RemoteSigned;
                }

                var powershell = Powershell.Create(initialState);

                // Scripts that aren't unblocked will prompt for permission to run on Windows before executing,
                // even if the scripts are signed.  (Unsigned scripts simply won't run.)
                UnblockScripts(powershell, Path.Combine(AppContext.BaseDirectory, "TTK"));

                // Import ARM-TTK module.
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", Path.Combine(AppContext.BaseDirectory, "TTK", "arm-ttk.psd1"))
                    .Invoke();

                if (!powershell.HadErrors)
                {
                    // Save the runspace with TTK loaded
                    this.runspace = powershell.Runspace;
                }
                else
                {
                    LogPowerShellErrors(powershell.Streams.Error, "There was an error initializing TTK.");
                }
            }
            catch (Exception e)
            {
                this.logger?.LogError(e, "There was an exception while initializing TTK.");
            }
        }

        /// <summary>
        /// Analyzes a template against the rules encoded in PowerShell.
        /// </summary>
        /// <param name="templateContext">The context of the template under analysis.
        /// <see cref="TemplateContext.TemplateIdentifier"/> must be the file path of the template to evaluate.</param>
        /// <returns>The <see cref="IEvaluation"/>s of the PowerShell rules against the template.</returns>
        public IEnumerable<IEvaluation> AnalyzeTemplate(TemplateContext templateContext)
        {
            if (templateContext?.TemplateIdentifier == null)
            {
                throw new ArgumentException($"{nameof(TemplateContext.TemplateIdentifier)} must not be null.", nameof(templateContext));
            }

            if (runspace == null)
            {
                // There was an error loading the TTK module.  Return an empty collection.
                logger?.LogWarning("Unable to run PowerShell based checks.  Initialization failed.");
                return Enumerable.Empty<IEvaluation>();
            }

            var executionResults = Powershell.Create(runspace)
                .AddCommand("Test-AzTemplate")
                .AddParameter("Test", "deploymentTemplate")
                .AddParameter("TemplatePath", templateContext.TemplateIdentifier)
                .Invoke();

            var evaluations = new List<PowerShellRuleEvaluation>();

            foreach (dynamic executionResult in executionResults)
            {
                var uniqueErrors = new Dictionary<string, SortedSet<int>>(); // Maps error messages to a sorted set of line numbers

                foreach (dynamic warning in executionResult.Warnings)
                {
                    PreProcessErrors(warning, uniqueErrors);
                }

                foreach (dynamic error in executionResult.Errors)
                {
                    PreProcessErrors(error, uniqueErrors);
                }

                foreach (KeyValuePair<string, SortedSet<int>> uniqueError in uniqueErrors)
                {
                    var ruleId = (executionResult.Name as string)?.Replace(" ", "");
                    ruleId = !String.IsNullOrEmpty(ruleId) ? ruleId : "TTK";
                    var ruleDescription = executionResult.Name + ". " + uniqueError.Key;

                    foreach (int lineNumber in uniqueError.Value)
                    {
                        evaluations.Add(new PowerShellRuleEvaluation(ruleId, ruleDescription, false, new PowerShellRuleResult(false, lineNumber)));
                    }
                }
            }

            return evaluations;
        }

        private void PreProcessErrors(dynamic error, Dictionary<string, SortedSet<int>> uniqueErrors)
        {
            var lineNumber = 0;

            Type errorType = error.GetType();
            IEnumerable<PropertyInfo> errorProperties = errorType.GetRuntimeProperties();
            if (errorProperties.Where(prop => prop.Name == "TargetObject").Any())
            {
                if (error.TargetObject is PSObject targetObject && targetObject.Properties["lineNumber"] != null)
                {
                    lineNumber = error.TargetObject.lineNumber;
                }
            }

            var errorMessage = lineNumberRegex.Replace(error.ToString(), string.Empty); 

            if (!uniqueErrors.TryAdd(errorMessage, new SortedSet<int> { lineNumber }))
            {
                // errorMessage was already added to the dictionary
                uniqueErrors[errorMessage].Add(lineNumber);
            }
        }

        /// <summary>
        /// Unblocks scripts on Windows to allow them to run.
        /// If a script is not unblocked, even if it's signed,
        /// PowerShell prompts for confirmation before executing.
        /// This prompting would throw an exception, because there's
        /// no interaction with a user that would allow for confirmation.
        /// </summary>
        private void UnblockScripts(Powershell powershell, string directory)
        {
            if (Platform.IsWindows)
            {
                powershell
                    .AddCommand("Get-ChildItem")
                    .AddParameter("Path", directory)
                    .AddParameter("Recurse")
                    .AddCommand("Unblock-File")
                    .Invoke();

                if (powershell.HadErrors)
                {
                    LogPowerShellErrors(powershell.Streams.Error, $"There was an error unblocking scripts in path '{directory}'.");
                }

                powershell.Commands.Clear();
            }
        }

        private void LogPowerShellErrors(PSDataCollection<ErrorRecord> errors, string summary)
        {
            this.logger?.LogError(summary);
            foreach (var error in errors)
            {
                this.logger?.LogError(error.ErrorDetails.Message);
            }
        }
    }
}