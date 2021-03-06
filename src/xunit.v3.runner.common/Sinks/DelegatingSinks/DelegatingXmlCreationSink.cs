﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Xunit.Abstractions;
using Xunit.Internal;
using Xunit.Runner.v2;
using Xunit.v3;

namespace Xunit.Runner.Common
{
	/// <summary>
	/// A delegating implementation of <see cref="IExecutionSink"/> which is responsible for
	/// creating the xUnit.net v2/v3 XML output from the execution test results.
	/// </summary>
	public class DelegatingXmlCreationSink : LongLivedMarshalByRefObject, IExecutionSink
	{
		readonly XElement assemblyElement;
		bool disposed;
		readonly XElement errorsElement;
		readonly IExecutionSink innerSink;
		readonly Dictionary<string, XElement> testCollectionElements = new Dictionary<string, XElement>();

		/// <summary>
		/// Initializes a new instance of the <see cref="DelegatingXmlCreationSink"/> class.
		/// </summary>
		/// <param name="innerSink"></param>
		/// <param name="assemblyElement"></param>
		public DelegatingXmlCreationSink(
			IExecutionSink innerSink,
			XElement assemblyElement)
		{
			Guard.ArgumentNotNull(nameof(innerSink), innerSink);
			Guard.ArgumentNotNull(nameof(assemblyElement), assemblyElement);

			this.innerSink = innerSink;
			this.assemblyElement = assemblyElement;

			errorsElement = new XElement("errors");
			assemblyElement.Add(errorsElement);
		}

		/// <inheritdoc/>
		public ExecutionSummary ExecutionSummary => innerSink.ExecutionSummary;

		/// <inheritdoc/>
		public ManualResetEvent Finished => innerSink.Finished;

		/// <inheritdoc/>
		public bool OnMessage(IMessageSinkMessage message)
		{
			Guard.ArgumentNotNull(nameof(message), message);

			// Call the inner sink first, because we want to be able to depend on ExecutionSummary
			// being correctly filled out.
			var result = innerSink.OnMessage(message);
			var messageTypes = default(HashSet<string>);  // TODO temporary

			return message.Dispatch<IErrorMessage>(messageTypes, HandleErrorMessage)
				&& message.Dispatch<ITestAssemblyCleanupFailure>(messageTypes, HandleTestAssemblyCleanupFailure)
				&& message.Dispatch<_TestAssemblyFinished>(messageTypes, HandleTestAssemblyFinished)
				&& message.Dispatch<_TestAssemblyStarting>(messageTypes, HandleTestAssemblyStarting)
				&& message.Dispatch<ITestCaseCleanupFailure>(messageTypes, HandleTestCaseCleanupFailure)
				&& message.Dispatch<ITestClassCleanupFailure>(messageTypes, HandleTestClassCleanupFailure)
				&& message.Dispatch<ITestCleanupFailure>(messageTypes, HandleTestCleanupFailure)
				&& message.Dispatch<ITestCollectionCleanupFailure>(messageTypes, HandleTestCollectionCleanupFailure)
				&& message.Dispatch<_TestCollectionFinished>(messageTypes, HandleTestCollectionFinished)
				&& message.Dispatch<_TestCollectionStarting>(messageTypes, HandleTestCollectionStarting)
				&& message.Dispatch<ITestFailed>(messageTypes, HandleTestFailed)
				&& message.Dispatch<ITestMethodCleanupFailure>(messageTypes, HandleTestMethodCleanupFailure)
				&& message.Dispatch<ITestPassed>(messageTypes, HandleTestPassed)
				&& message.Dispatch<ITestSkipped>(messageTypes, HandleTestSkipped)
				&& result;
		}

		void AddError(
			string type,
			string? name,
			IFailureInformation failureInfo)
		{
			var errorElement = new XElement("error", new XAttribute("type", type), CreateFailureElement(failureInfo));
			if (name != null)
				errorElement.Add(new XAttribute("name", name));

			errorsElement.Add(errorElement);
		}

		static XElement CreateFailureElement(IFailureInformation failureInfo) =>
			new XElement("failure",
				new XAttribute("exception-type", failureInfo.ExceptionTypes[0]),
				new XElement("message", new XCData(XmlEscape(ExceptionUtility.CombineMessages(failureInfo)))),
				new XElement("stack-trace", new XCData(ExceptionUtility.CombineStackTraces(failureInfo) ?? string.Empty))
			);

		XElement CreateTestResultElement(
			ITestResultMessage testResult,
			string resultText)
		{
			var test = testResult.Test;
			var testCase = testResult.TestCase;
			var testMethod = testCase.TestMethod;
			var testClass = testMethod.TestClass;

			// TODO: This is broken because that's the wrong unique ID
			var collectionElement = GetTestCollectionElement(testClass.TestCollection.UniqueID.ToString());
			var testResultElement =
				new XElement("test",
					new XAttribute("name", XmlEscape(test.DisplayName)),
					new XAttribute("type", testClass.Class.Name),
					new XAttribute("method", testMethod.Method.Name),
					new XAttribute("time", testResult.ExecutionTime.ToString(CultureInfo.InvariantCulture)),
					new XAttribute("result", resultText)
				);

			var testOutput = testResult.Output;
			if (!string.IsNullOrWhiteSpace(testOutput))
				testResultElement.Add(new XElement("output", new XCData(testOutput)));

			var sourceInformation = testCase.SourceInformation;
			if (sourceInformation != null)
			{
				var fileName = sourceInformation.FileName;
				if (fileName != null)
					testResultElement.Add(new XAttribute("source-file", fileName));

				var lineNumber = sourceInformation.LineNumber;
				if (lineNumber != null)
					testResultElement.Add(new XAttribute("source-line", lineNumber.GetValueOrDefault()));
			}

			var traits = testCase.Traits;
			if (traits != null && traits.Count > 0)
			{
				var traitsElement = new XElement("traits");

				foreach (var keyValuePair in traits)
					foreach (var val in keyValuePair.Value)
						traitsElement.Add(
							new XElement("trait",
								new XAttribute("name", XmlEscape(keyValuePair.Key)),
								new XAttribute("value", XmlEscape(val))
							)
						);

				testResultElement.Add(traitsElement);
			}

			collectionElement.Add(testResultElement);

			return testResultElement;
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			if (disposed)
				throw new ObjectDisposedException(GetType().FullName);

			disposed = true;

			innerSink.Dispose();
		}

		XElement GetTestCollectionElement(string testCollectionUniqueID)
		{
			lock (testCollectionElements)
				return testCollectionElements.GetOrAdd(testCollectionUniqueID, () => new XElement("collection"));
		}

		void HandleErrorMessage(MessageHandlerArgs<IErrorMessage> args)
			=> AddError("fatal", null, args.Message);

		void HandleTestAssemblyCleanupFailure(MessageHandlerArgs<ITestAssemblyCleanupFailure> args)
			=> AddError("assembly-cleanup", args.Message.TestAssembly.Assembly.AssemblyPath, args.Message);

		void HandleTestAssemblyFinished(MessageHandlerArgs<_TestAssemblyFinished> args)
		{
			assemblyElement.Add(
				new XAttribute("total", ExecutionSummary.Total),
				new XAttribute("passed", ExecutionSummary.Total - ExecutionSummary.Failed - ExecutionSummary.Skipped),
				new XAttribute("failed", ExecutionSummary.Failed),
				new XAttribute("skipped", ExecutionSummary.Skipped),
				new XAttribute("time", ExecutionSummary.Time.ToString("0.000", CultureInfo.InvariantCulture)),
				new XAttribute("errors", ExecutionSummary.Errors)
			);

			foreach (var element in testCollectionElements.Values)
				assemblyElement.Add(element);
		}

		void HandleTestAssemblyStarting(MessageHandlerArgs<_TestAssemblyStarting> args)
		{
			var assemblyStarting = args.Message;
			assemblyElement.Add(
				new XAttribute("name", assemblyStarting.AssemblyPath),
				new XAttribute("environment", assemblyStarting.TestEnvironment),
				new XAttribute("test-framework", assemblyStarting.TestFrameworkDisplayName),
				new XAttribute("run-date", assemblyStarting.StartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
				new XAttribute("run-time", assemblyStarting.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
			);

			if (assemblyStarting.ConfigFilePath != null)
				assemblyElement.Add(new XAttribute("config-file", assemblyStarting.ConfigFilePath));
			if (assemblyStarting.TargetFramework != null)
				assemblyElement.Add(new XAttribute("target-framework", assemblyStarting.TargetFramework));
		}

		void HandleTestCaseCleanupFailure(MessageHandlerArgs<ITestCaseCleanupFailure> args)
			=> AddError("test-case-cleanup", args.Message.TestCase.DisplayName, args.Message);

		void HandleTestClassCleanupFailure(MessageHandlerArgs<ITestClassCleanupFailure> args)
			=> AddError("test-class-cleanup", args.Message.TestClass.Class.Name, args.Message);

		void HandleTestCleanupFailure(MessageHandlerArgs<ITestCleanupFailure> args)
			=> AddError("test-cleanup", args.Message.Test.DisplayName, args.Message);

		void HandleTestCollectionCleanupFailure(MessageHandlerArgs<ITestCollectionCleanupFailure> args)
			=> AddError("test-collection-cleanup", args.Message.TestCollection.DisplayName, args.Message);

		void HandleTestCollectionFinished(MessageHandlerArgs<_TestCollectionFinished> args)
		{
			var testCollectionFinished = args.Message;
			var collectionElement = GetTestCollectionElement(testCollectionFinished.TestCollectionUniqueID);
			collectionElement.Add(
				new XAttribute("total", testCollectionFinished.TestsRun),
				new XAttribute("passed", testCollectionFinished.TestsRun - testCollectionFinished.TestsFailed - testCollectionFinished.TestsSkipped),
				new XAttribute("failed", testCollectionFinished.TestsFailed),
				new XAttribute("skipped", testCollectionFinished.TestsSkipped),
				new XAttribute("time", testCollectionFinished.ExecutionTime.ToString("0.000", CultureInfo.InvariantCulture))
			);
		}

		void HandleTestCollectionStarting(MessageHandlerArgs<_TestCollectionStarting> args)
		{
			var testCollectionStarting = args.Message;
			var collectionElement = GetTestCollectionElement(testCollectionStarting.TestCollectionUniqueID);

			collectionElement.Add(new XAttribute("name", XmlEscape(testCollectionStarting.TestCollectionDisplayName)));
		}

		void HandleTestFailed(MessageHandlerArgs<ITestFailed> args)
		{
			var testFailed = args.Message;
			var testElement = CreateTestResultElement(testFailed, "Fail");
			testElement.Add(CreateFailureElement(testFailed));
		}

		void HandleTestMethodCleanupFailure(MessageHandlerArgs<ITestMethodCleanupFailure> args)
			=> AddError("test-method-cleanup", args.Message.TestMethod.Method.Name, args.Message);

		void HandleTestPassed(MessageHandlerArgs<ITestPassed> args)
			=> CreateTestResultElement(args.Message, "Pass");

		void HandleTestSkipped(MessageHandlerArgs<ITestSkipped> args)
		{
			var testSkipped = args.Message;
			var testElement = CreateTestResultElement(testSkipped, "Skip");
			testElement.Add(new XElement("reason", new XCData(XmlEscape(testSkipped.Reason))));
		}

		/// <summary>
		/// Escapes a string for placing into the XML.
		/// </summary>
		/// <param name="value">The value to be escaped.</param>
		/// <returns>The escaped value.</returns>
		static string XmlEscape(string? value)
		{
			if (value == null)
				return string.Empty;

			value =
				value
					.Replace("\\", "\\\\")
					.Replace("\r", "\\r")
					.Replace("\n", "\\n")
					.Replace("\t", "\\t")
					.Replace("\0", "\\0")
					.Replace("\a", "\\a")
					.Replace("\b", "\\b")
					.Replace("\v", "\\v")
					.Replace("\"", "\\\"")
					.Replace("\f", "\\f");

			var escapedValue = new StringBuilder(value.Length);
			for (var idx = 0; idx < value.Length; ++idx)
			{
				var ch = value[idx];
				if (ch < 32)
					escapedValue.Append($@"\x{(+ch).ToString("x2")}");
				else if (char.IsSurrogatePair(value, idx)) // Takes care of the case when idx + 1 == value.Length
				{
					escapedValue.Append(ch); // Append valid surrogate chars like normal
					escapedValue.Append(value[++idx]);
				}
				// Check for invalid chars and append them like \x----
				else if (char.IsSurrogate(ch) || ch == '\uFFFE' || ch == '\uFFFF')
					escapedValue.Append($@"\x{(+ch).ToString("x4")}");
				else
					escapedValue.Append(ch);
			}

			return escapedValue.ToString();
		}
	}
}
