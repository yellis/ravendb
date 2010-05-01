using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Transactions;
using log4net;
using Newtonsoft.Json.Linq;
using Raven.Client.Client;
using System;
using Raven.Client.Exceptions;
using Raven.Database;
using Raven.Database.Data;
using Raven.Database.Json;

namespace Raven.Client.Document
{
	public class DocumentSession : IDocumentSession
	{
		private readonly ILog log = LogManager.GetLogger(typeof (DocumentSession));
	    private const string TemporaryIdPrefix = "Temporary Id: ";
		private const string RavenEntityName = "Raven-Entity-Name";
		private readonly IDatabaseCommands database;
		private readonly DocumentStore documentStore;
        private readonly Dictionary<object, DocumentMetadata> entitiesAndMetadata = new Dictionary<object, DocumentMetadata>();
        private readonly Dictionary<string, object> entitiesByKey = new Dictionary<string, object>();

	    private readonly ISet<object> deletedEntities = new HashSet<object>();
	    private RavenClientEnlistment enlistment;

		public event Action<object> Stored;
        public string StoreIdentifier { get { return documentStore.Identifier; } }

		public DocumentSession(DocumentStore documentStore, IDatabaseCommands database)
		{
			this.documentStore = documentStore;
			this.database = database;
		    UseOptimisticConcurrency = false;
		}

		public T Load<T>(string id)
		{
		    object existingEntity;
		    if(entitiesByKey.TryGetValue(id, out existingEntity))
		    {
		        return (T)existingEntity;
		    }

			JsonDocument documentFound;
            try
            {
				log.DebugFormat("Loading document [{0}] from {1}", id, StoreIdentifier);
				documentFound = database.Get(id);
            }
            catch (WebException ex)
            {
            	var httpWebResponse = ex.Response as HttpWebResponse;
            	if (httpWebResponse != null && httpWebResponse.StatusCode == HttpStatusCode.NotFound)
                    return default(T);
            	throw;
            }
			if (documentFound == null)
				return default(T);

			return TrackEntity<T>(documentFound);
		}

		private T TrackEntity<T>(JsonDocument documentFound)
		{
			if(documentFound.Metadata.Property("@etag") == null)
			{
				documentFound.Metadata.Add("@etag", new JValue(documentFound.Etag.ToString()));
			}
			return TrackEntity<T>(documentFound.Key, documentFound.Data.ToJObject(), documentFound.Metadata);
		}

		public T TrackEntity<T>(string key, JObject document, JObject metadata)
	    {
			var entity = ConvertToEntity<T>(key, document);
			var etag = metadata.Value<string>("@etag");
			log.DebugFormat("Tracking document [{0}] from {1} as {2}. Etag: {3}", key, StoreIdentifier, typeof(T).FullName, etag);
			entitiesAndMetadata.Add(entity, new DocumentMetadata
	        {
				OriginalValue = document,
	            Metadata = metadata,
				ETag = new Guid(etag),
	            Key = key
	        });
	        entitiesByKey[key] = entity;
	        return (T) entity;
	    }

	    public T[] Load<T>(params string[] ids)
	    {
			if(log.IsDebugEnabled)
				log.DebugFormat("Bulk loading ids [{0}] from {1}", string.Join(", ", ids), StoreIdentifier);
	        return documentStore.DatabaseCommands.Get(ids)
                .Select(TrackEntity<T>).ToArray();
	    }

	    public void Delete<T>(T entity)
	    {
	        deletedEntities.Add(entity);
	    }

	    private object ConvertToEntity<T>(string id, JObject documentFound)
		{
	    	var entity = documentFound.Deserialize<T>();

			foreach (var property in entity.GetType().GetProperties())
			{
				var isIdentityProperty = documentStore.Conventions.FindIdentityProperty.Invoke(property);
				if (isIdentityProperty)
					property.SetValue(entity, id, null);
			}
			return entity;
		}

		public void Store<T>(T entity)
		{
            var identityProperty = GetIdentityProperty(typeof(T));
            var id = identityProperty.GetValue(entity, null) as string;
			if (id != null &&
				id.EndsWith("/") == false && // not a prefix id
				entitiesByKey.ContainsKey(id))
			{
				if (ReferenceEquals(entitiesByKey[id], entity))
					return;// calling Store twice on the same reference is a no-op
				throw new NonUniqueObjectException("Attempted to associated a different object with id '" + id + "'.");
			}

			var tag = documentStore.Conventions.FindTypeTagName(typeof(T));
			entitiesAndMetadata.Add(entity, new DocumentMetadata
			{
                Key = id,
                Metadata = new JObject(new JProperty(RavenEntityName, new JValue(tag))),
                ETag = null,
				OriginalValue = new JObject()
			});
			if (id != null)
				entitiesByKey[id] = entity;
		}

		public void Evict<T>(T entity)
		{
		    DocumentMetadata value;
		    if(entitiesAndMetadata.TryGetValue(entity, out value))
		    {
		        entitiesAndMetadata.Remove(entity);
		        entitiesByKey.Remove(value.Key);
		    }
		    deletedEntities.Remove(entity);
		}

		private ICommandData CreatePutEntityCommand(object entity, DocumentMetadata documentMetadata)
		{
			var json = ConvertEntityToJson(entity);
			var entityType = entity.GetType();
			var identityProperty = GetIdentityProperty(entityType);

            var key = (string)identityProperty.GetValue(entity, null);
            if (key == null || key.StartsWith(TemporaryIdPrefix))
            {
				if (key != null)
					entitiesByKey.Remove(key);
            	key = documentStore.Conventions.GenerateDocumentKey(entity);
            }
		    var etag = UseOptimisticConcurrency ? documentMetadata.ETag : null;

			return new PutCommandData
			{
				Document = json,
				Etag = etag,
				Key = key,
				Metadata = documentMetadata.Metadata,
			};
		}

		private PropertyInfo GetIdentityProperty(Type entityType)
		{
			var identityProperty = entityType.GetProperties()
				.FirstOrDefault(q => documentStore.Conventions.FindIdentityProperty(q));

			if(identityProperty == null)
				throw new InvalidOperationException("Could not find id proeprty for " + entityType.Name);
			return identityProperty;
		}

		public void SaveChanges()
		{
            if(enlistment == null && Transaction.Current != null)
            {
                enlistment = new RavenClientEnlistment(this, Transaction.Current.TransactionInformation.DistributedIdentifier);
                Transaction.Current.EnlistVolatile(enlistment,EnlistmentOptions.None);
            }
			var entities = new List<object>();
			var cmds = new List<ICommandData>();
            foreach (var key in (from deletedEntity in deletedEntities
                                 let identityProperty = GetIdentityProperty(deletedEntity.GetType())
                                 select identityProperty.GetValue(deletedEntity, null))
                                 .OfType<string>())
            {
                Guid? etag = null;
                object existingEntity;
                if (entitiesByKey.TryGetValue(key, out existingEntity))
                {
                    DocumentMetadata metadata;
                    if (entitiesAndMetadata.TryGetValue(existingEntity, out metadata))
                        etag = metadata.ETag;
                    entitiesAndMetadata.Remove(existingEntity);
                }

                etag = UseOptimisticConcurrency ? etag : null;
            	entities.Add(existingEntity);
                cmds.Add(new DeleteCommandData
                {
                	Etag = etag,
					Key = key,
                });
            }
            deletedEntities.Clear();
		    foreach (var entity in entitiesAndMetadata.Where(EntityChanged))
			{
				entities.Add(entity.Key);
				if (entity.Value.Key != null)
					entitiesByKey.Remove(entity.Value.Key);
				cmds.Add(CreatePutEntityCommand(entity.Key, entity.Value));
			}
			
			if (cmds.Count == 0)
				return;

			log.DebugFormat("Saving {0} changes to {1}", cmds.Count, StoreIdentifier);
			UpdateBatchResults(database.Batch(cmds.ToArray()), entities);
		}

		private void UpdateBatchResults(IList<BatchResult> batchResults, IList<object> entities)
		{
			var stored = Stored;
			for (int i = 0; i < batchResults.Count; i++)
			{
				var batchResult = batchResults[i];
				if (batchResult.Method != "PUT")
					continue;

				var entity = entities[i];
				DocumentMetadata documentMetadata;
				if (entitiesAndMetadata.TryGetValue(entity, out documentMetadata) == false)
					continue;

				entitiesByKey[batchResult.Key] = entity;
				documentMetadata.ETag = batchResult.Etag;
				documentMetadata.Key = batchResult.Key;
				documentMetadata.OriginalValue = ConvertEntityToJson(entity);

				GetIdentityProperty(entity.GetType())
					.SetValue(entity, batchResult.Key, null);

				if (stored != null)
					stored(entity);
			}
		}

		private bool EntityChanged(KeyValuePair<object, DocumentMetadata> kvp)
		{
			var newObj = ConvertEntityToJson(kvp.Key);
			if (kvp.Value == null)
				return true;
			return new JTokenEqualityComparer().Equals(newObj, kvp.Value.OriginalValue) == false;
		}

		private JObject ConvertEntityToJson(object entity)
		{
			var identityProperty = entity.GetType().GetProperties()
				.FirstOrDefault(q => documentStore.Conventions.FindIdentityProperty.Invoke(q));

			var objectAsJson = JObject.FromObject(entity);
			if (identityProperty != null)
			{
				objectAsJson.Remove(identityProperty.Name);
			}

			objectAsJson.Add("type", JToken.FromObject(entity.GetType().FullName));
			return objectAsJson;
		}

		public void Clear()
		{
			entitiesAndMetadata.Clear();
		}

	    public bool UseOptimisticConcurrency
	    {
	        get; set;
	    }

	    public IDocumentQuery<T> Query<T>(string indexName)
		{
	    	return new DocumentQuery<T>(this, database, indexName, null);
		}

        #region IDisposable Members

        public void Dispose()
        {
            //dereference all event listeners
            Stored = null;
        }

        #endregion

	    public void Commit(Guid txId)
	    {
	        documentStore.DatabaseCommands.Commit(txId);
	        enlistment = null;
	    }

	    public void Rollback(Guid txId)
	    {
	        documentStore.DatabaseCommands.Rollback(txId);
            enlistment = null;
	    }

        public class DocumentMetadata
        {
			public JObject OriginalValue { get; set; }
            public JObject Metadata { get; set; }
            public Guid? ETag { get; set; }
            public string Key { get; set; }
        }
	}
}