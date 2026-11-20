using System;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Marks a text field (or a list of texts) that holds an expression: the shared inspector drawer
    /// checks it as it is typed — the syntax, the roots and functions its place serves (<see cref="Context"/>, a key of
    /// <c>SimuLean.Expressions.ExpressionContext</c>) and the elements it names in the scene — and shows the error, or a check
    /// mark, under it; <c>simulean_set</c> / <c>simulean_apply</c> refuse a text that does not parse. With
    /// <see cref="Records"/>, each text is a <c>name = expression</c> record (labels, tallies, member properties). Every
    /// expression field carries it (an EditMode test checks).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SimuLeanExpressionAttribute : PropertyAttribute
    {
        /// <summary>The <c>ExpressionContext</c> key: <c>time</c>, <c>priority</c>, <c>routing</c>, <c>admission</c>,
        /// <c>label</c>, <c>tally</c>, <c>maxWait</c>, <c>agenda</c>, <c>callOff</c>, <c>sequencing</c>, <c>milkRunFilter</c>,
        /// <c>memberProperty</c>, <c>listField</c>, <c>requestField</c>, <c>reaction</c>, <c>fitness</c>.</summary>
        public string Context { get; }

        /// <summary>Each text is a <c>name = expression</c> record.</summary>
        public bool Records { get; set; }

        public SimuLeanExpressionAttribute(string context) { Context = context; }
    }
}
