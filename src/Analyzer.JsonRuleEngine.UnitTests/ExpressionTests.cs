﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine.Expressions;
using Microsoft.Azure.Templates.Analyzer.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine.UnitTests
{
    [TestClass]
    public class ExpressionTests
    {
        /// <summary>
        /// A mock implementation of an <see cref="Expression"/> for testing internal methods.
        /// </summary>
        private class MockExpression : Expression
        {
            private Func<IJsonPathResolver, JsonRuleEvaluation> evaluationCallback;

            public MockExpression(ExpressionCommonProperties commonProperties, Func<IJsonPathResolver, JsonRuleEvaluation> evaluationCallback)
                : base(commonProperties)
            {
                this.evaluationCallback = evaluationCallback;
            }

            public override JsonRuleEvaluation Evaluate(IJsonPathResolver jsonScope)
            {
                return base.EvaluateInternal(jsonScope, evaluationCallback);
            }
        }

        [TestMethod]
        public void Evaluate_WhereConditionFalse_PathNotEvaluated()
        {
            var mockPathResolver = new Mock<IJsonPathResolver>();
            mockPathResolver
                .Setup(r => r.Resolve(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });
            mockPathResolver
                .Setup(r => r.ResolveResourceType(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });

            bool whereConditionWasEvaluated = false;

            // Create a mock expression for the Where condition.
            // It will return an Evaluation that has results, but Passed is false.
            var whereExpression = new MockExpression(new ExpressionCommonProperties(), pathResolver =>
            {
                whereConditionWasEvaluated = true;
                return new JsonRuleEvaluation(null, passed: false, results: new[] { new JsonRuleResult() });
            });

            // A top level mocked expression that contains a Where condition.
            var mockExpression = new MockExpression(new ExpressionCommonProperties { ResourceType = "ResourceProvider/resource", Path = "some.path", Where = whereExpression }, pathResolver =>
            {
                // This expression should not evaluate anything because the Where condition does not pass
                Assert.Fail("Top-level expression was evaluated even though the Where condition did not pass.");
                return null;
            });

            mockExpression.Evaluate(mockPathResolver.Object);

            Assert.IsTrue(whereConditionWasEvaluated);
        }

        [TestMethod]
        public void Evaluate_WhereConditionTrueWithNoResults_PathNotEvaluated()
        {
            var mockPathResolver = new Mock<IJsonPathResolver>();
            mockPathResolver
                .Setup(r => r.Resolve(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });
            mockPathResolver
                .Setup(r => r.ResolveResourceType(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });

            bool whereConditionWasEvaluated = false;

            // Create a mock expression for the Where condition.
            // It will return an Evaluation that Passed, but contains no results (i.e. no paths were evaluated).
            var whereExpression = new MockExpression(new ExpressionCommonProperties(), pathResolver =>
            {
                whereConditionWasEvaluated = true;
                return new JsonRuleEvaluation(null, passed: true, results: new JsonRuleResult[0]);
            });

            // A top level mocked expression that contains a Where condition.
            var mockExpression = new MockExpression(new ExpressionCommonProperties { ResourceType = "ResourceProvider/resource", Path = "some.path", Where = whereExpression }, pathResolver =>
            {
                // This expression should not evaluate anything because the Where condition does not have any results.
                Assert.Fail("Top-level expression was evaluated even though the Where condition contains no results.");
                return null;
            });

            mockExpression.Evaluate(mockPathResolver.Object);

            Assert.IsTrue(whereConditionWasEvaluated);
        }

        [TestMethod]
        public void Evaluate_WhereConditionTrue_PathIsEvaluated()
        {
            var mockPathResolver = new Mock<IJsonPathResolver>();
            mockPathResolver
                .Setup(r => r.Resolve(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });
            mockPathResolver
                .Setup(r => r.ResolveResourceType(It.IsAny<string>()))
                .Returns(() => new[] { mockPathResolver.Object });

            bool whereConditionWasEvaluated = false;
            bool topLevelExpressionWasEvaluated = false;

            // Create a mock expression for the Where condition.
            // It will return an Evaluation that Passed, but contains no results (i.e. no paths were evaluated).
            var whereExpression = new MockExpression(new ExpressionCommonProperties(), pathResolver =>
            {
                whereConditionWasEvaluated = true;
                return new JsonRuleEvaluation(null, passed: true, results: new[] { new JsonRuleResult() });
            });

            // A top level mocked expression that contains a Where condition.
            var mockExpression = new MockExpression(new ExpressionCommonProperties { ResourceType = "ResourceProvider/resource", Path = "some.path", Where = whereExpression }, pathResolver =>
            {
                // Track whether this was evaluated or not.
                topLevelExpressionWasEvaluated = true;
                return new JsonRuleEvaluation(null, passed: true, results: new JsonRuleResult[0]);
            });

            mockExpression.Evaluate(mockPathResolver.Object);

            Assert.IsTrue(whereConditionWasEvaluated);
            Assert.IsTrue(topLevelExpressionWasEvaluated);
        }
    }
}
