//-----------------------------------------------------------------------
// <copyright file="AbstractViewGenerator.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using Lucene.Net.Documents;
using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using System.Linq;
using Raven.Database.Indexing;
using Spatial4n.Core.Shapes;

namespace Raven.Database.Linq
{
	/// <summary>
	/// This class represents a base class for all "Views" we generate and compile on the fly - all
	/// Map and MapReduce indexes are being re-written into this class and then compiled and executed
	/// against the data in RavenDB
	/// </summary>
	[InheritedExport]
	public abstract class AbstractViewGenerator
	{
		private readonly HashSet<string> fields = new HashSet<string>();
		private bool? containsProjection;
		private int? countOfSelectMany;
		private bool? hasWhereClause;
		private readonly HashSet<string> mapFields = new HashSet<string>();
		private readonly HashSet<string> reduceFields = new HashSet<string>();

		private static readonly Regex selectManyOrFrom = new Regex(@"( (?<!^)\s from \s ) | ( \.SelectMany\( )",
			RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
		private IndexDefinition indexDefinition;

		public string SourceCode { get; set; }

		public int CountOfSelectMany
		{
			get
			{
				if (countOfSelectMany == null)
				{
					countOfSelectMany = selectManyOrFrom.Matches(ViewText).Count;
				}
				return countOfSelectMany.Value;
			}
		}

		public int CountOfFields { get { return fields.Count; } }

		public List<IndexingFunc> MapDefinitions { get; private set; }

		public IndexingFunc ReduceDefinition { get; set; }

		public TranslatorFunc TransformResultsDefinition { get; set; }

		public GroupByKeyFunc GroupByExtraction { get; set; }

		public string ViewText { get; set; }

		public IDictionary<string, FieldStorage> Stores { get; set; }

		public IDictionary<string, FieldIndexing> Indexes { get; set; }

		public IDictionary<string, FieldTermVector> TermVectors { get; set; } 

		public IDictionary<string, SpatialOptions> SpatialIndexes { get; set; }

		public HashSet<string> ForEntityNames { get; set; }

		public string[] Fields
		{
			get { return fields.ToArray(); }
		}

		public bool HasWhereClause
		{
			get
			{
				if (hasWhereClause == null)
				{
					hasWhereClause = ViewText.IndexOf("where", StringComparison.OrdinalIgnoreCase) > -1;
				}
				return hasWhereClause.Value;
			}
		}

		protected AbstractViewGenerator()
		{
			MapDefinitions = new List<IndexingFunc>();
			ForEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Stores = new Dictionary<string, FieldStorage>();
			Indexes = new Dictionary<string, FieldIndexing>();
			TermVectors = new Dictionary<string, FieldTermVector>();
			SpatialIndexes = new Dictionary<string, SpatialOptions>();
			SpatialFields = new ConcurrentDictionary<string, SpatialField>();
		}

		public void Init(IndexDefinition definition)
		{
			indexDefinition = definition;
		}

		protected IEnumerable<AbstractField> CreateField(string name, object value, bool stored = false, bool analyzed = true)
		{
			return new AnonymousObjectToLuceneDocumentConverter(indexDefinition, this)
				.CreateFields(name, value, stored ? Field.Store.YES : Field.Store.NO);
		}

		protected dynamic LoadDocument(string key)
		{
			if (CurrentIndexingScope.Current == null)
				throw new InvalidOperationException("LoadDocument may only be called from the map portion of the index. Was called with: " + key);

			return CurrentIndexingScope.Current.LoadDocument(key);
		}

		public void AddQueryParameterForMap(string field)
		{
			mapFields.Add(field);
		}

		public void AddQueryParameterForReduce(string field)
		{
			reduceFields.Add(field);
		}

		public void AddField(string field)
		{
			fields.Add(field);
		}

		public virtual bool ContainsFieldOnMap(string field)
		{
			if (field.EndsWith("_Range")) field = field.Substring(0, field.Length - 6);
			if (ReduceDefinition == null)
				return fields.Contains(field);
			return mapFields.Contains(field);
		}

		public virtual bool ContainsField(string field)
		{
			if (fields.Contains(field))
				return true;
			if (containsProjection == null)
			{
				containsProjection = ViewText != null && ViewText.Contains("Project(");
			}
			return containsProjection.Value;
		}

		protected void AddMapDefinition(IndexingFunc mapDef)
		{
			MapDefinitions.Add(mapDef);
		}

		protected IEnumerable<dynamic> Recurse(object item, Func<dynamic, dynamic> func)
		{
			return new RecursiveFunction(item, func).Execute();
		}

		#region Spatial index

		private ConcurrentDictionary<string, SpatialField> SpatialFields { get; set; }

		public IEnumerable<IFieldable> SpatialGenerate(double? lat, double? lng)
		{
			return SpatialGenerate(Constants.DefaultSpatialFieldName, lat, lng);
		}

		public IEnumerable<IFieldable> SpatialGenerate(string fieldName, double? lat, double? lng)
		{
			var spatialField = GetSpatialField(fieldName);

			if (lng == null || double.IsNaN(lng.Value))
				return Enumerable.Empty<IFieldable>();
			if(lat == null || double.IsNaN(lat.Value))
				return Enumerable.Empty<IFieldable>();

			Shape shape = spatialField.GetContext().MakePoint(lng.Value, lat.Value);
			return spatialField.CreateIndexableFields(shape);
		}

		public IEnumerable<IFieldable> SpatialGenerate(string fieldName, string shapeWKT,
			SpatialSearchStrategy spatialSearchStrategy = SpatialSearchStrategy.GeohashPrefixTree,
			int maxTreeLevel = 0, double distanceErrorPct = 0.025)
		{
			var spatialField = GetSpatialField(fieldName, spatialSearchStrategy, maxTreeLevel);
			return spatialField.CreateIndexableFields(shapeWKT);
		}

		[CLSCompliant(false)]
		public SpatialField GetSpatialField(string fieldName, SpatialSearchStrategy spatialSearchStrategy = SpatialSearchStrategy.GeohashPrefixTree, int maxTreeLevel = 0)
		{
			return SpatialFields.GetOrAdd(fieldName, s =>
			{
				if (SpatialFields.Count > 1024)
					throw new InvalidOperationException("The number of spatial fields in an index is limited to 1,024");
				
				SpatialOptions opt;
				indexDefinition.SpatialIndexes.TryGetValue(fieldName, out opt);

				if (opt == null)
					opt = SpatialOptionsFactory.FromLegacy(spatialSearchStrategy, maxTreeLevel);

				return new SpatialField(fieldName, opt);
			});
		}

		public bool IsSpatialField(string fieldName)
		{
			if (indexDefinition == null || indexDefinition.SpatialIndexes == null)
				return false;

			SpatialOptions opt;
			indexDefinition.SpatialIndexes.TryGetValue(fieldName, out opt);
			if (opt == null)
				return false;

			SpatialFields.GetOrAdd(fieldName, s =>
			{
				if (SpatialFields.Count > 1024)
					throw new InvalidOperationException("The number of spatial fields in an index is limited to 1,024");

				return new SpatialField(fieldName, opt);
			});

			return true;
		}

		#endregion
	}
}
