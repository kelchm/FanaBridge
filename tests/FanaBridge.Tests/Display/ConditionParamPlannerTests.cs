using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E7 C: condition-param planner — host-local, no wire budget, HasEncoder gate.
    /// </summary>
    public class ConditionParamPlannerTests
    {
        private static DisplayConfigV2 EmptyDoc()
            => new DisplayConfigV2();

        private static DisplayConfigV2 DocWithConditionParams(params ushort[] paramIds)
        {
            var doc = new DisplayConfigV2();
            doc.Priority = new PriorityLadder();
            var summons = new List<Summon>();
            foreach (ushort pid in paramIds)
            {
                summons.Add(new Summon
                {
                    Id = "s-" + pid,
                    Condition = new Condition
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.ItmField,
                            Name = pid.ToString(),
                        },
                        Operator = ConditionOperator.GreaterThan,
                        Value = 0,
                    },
                });
            }
            doc.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s-test",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                Summons = summons,
            });
            return doc;
        }

        [Fact]
        public void PublishesAllConditionParams_NoBudgetDrop()
        {
            // More than 16 condition refs — all kept (host-local, zero wire cost).
            var ids = Enumerable.Range(1, 20).Select(i => (ushort)(100 + i)).ToArray();
            var doc = DocWithConditionParams(ids);
            var plan = ConditionParamPlanner.Plan(doc, hasEncoder: _ => true);
            Assert.Equal(20, plan.ParamIds.Count);
            Assert.False(plan.Degraded);
            Assert.Empty(plan.NoEncoderParams);
            Assert.Equal(ids, plan.ParamIds.ToArray());
        }

        [Fact]
        public void NoEncoder_DegradesVisibly()
        {
            var doc = DocWithConditionParams(5, 9999, 25);
            var warns = new List<string>();
            var plan = ConditionParamPlanner.Plan(
                doc,
                hasEncoder: pid => pid != 9999,
                warn: warns.Add);
            Assert.Equal(new ushort[] { 5, 25 }, plan.ParamIds.ToArray());
            Assert.Equal(new ushort[] { 9999 }, plan.NoEncoderParams.ToArray());
            Assert.True(plan.Degraded);
            Assert.Single(warns);
        }

        [Fact]
        public void Duplicates_Collapse()
        {
            var doc = DocWithConditionParams(5, 5, 25);
            var plan = ConditionParamPlanner.Plan(doc, _ => true);
            Assert.Equal(new ushort[] { 5, 25 }, plan.ParamIds.ToArray());
        }

        [Fact]
        public void DegradedConditionRef_StillCollectedWhenParseable()
        {
            var doc = DocWithConditionParams(77);
            doc.Priority.Rows[0].Summons[0].Condition.Source.DegradedAtLoad = true;
            var plan = ConditionParamPlanner.Plan(doc, _ => true);
            Assert.Contains((ushort)77, plan.ParamIds);
        }

        [Fact]
        public void SelfItmField_Skipped_InDocumentCollect()
        {
            var doc = new DisplayConfigV2();
            doc.Fields = new Dictionary<ushort, FieldEntry>
            {
                [42] = new FieldEntry
                {
                    Overrides = new List<FieldOverride>
                    {
                        new FieldOverride
                        {
                            Id = "ov",
                            Condition = new Condition
                            {
                                Source = new ValueSource
                                {
                                    Kind = ValueSourceKind.ItmField,
                                    Name = "self",
                                },
                                Operator = ConditionOperator.GreaterThan,
                                Value = 1,
                            },
                        },
                    },
                },
            };
            var refs = ConditionParamPlanner.CollectConditionReferencedParams(doc);
            Assert.Empty(refs);
        }

        [Fact]
        public void EmptyDoc_EmptyPlan()
        {
            var plan = ConditionParamPlanner.Plan(EmptyDoc(), _ => true);
            Assert.Empty(plan.ParamIds);
            Assert.False(plan.Degraded);
        }

        [Fact]
        public void NullHasEncoder_TreatsAllAsEncodable()
        {
            var doc = DocWithConditionParams(1, 2, 3);
            var plan = ConditionParamPlanner.Plan(doc, hasEncoder: null);
            Assert.Equal(3, plan.ParamIds.Count);
        }
    }
}
