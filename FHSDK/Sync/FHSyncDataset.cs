﻿using System;
using System.Collections.Generic;
using FHSDK.Services;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics.Contracts;
using System.Xml;
using Newtonsoft.Json;
using System.Net;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;

namespace FHSDK.Sync
{
    public class FHSyncDataset<T> where T : IFHSyncModel
    {
        private const string LOG_TAG = "FHSyncDataset";
        private const string PERSIST_FILE_NAME = ".sync.json";
        private const string DATA_PERSIST_FILE_NAME = ".data.json";
        private const string PENDING_DATA_PERSIST_FILE_NAME = ".pendings.json";
        /// <summary>
        /// If the sync loop is running
        /// </summary>
        private Boolean syncRunning = false;
        /// <summary>
        /// <summary>
        /// Is there any pending sync records
        /// </summary>
        private Boolean syncPending = false;
        /// The store of pending records
        /// </summary>
        private IDataStore<FHSyncPendingRecord<T>> pendingRecords;
        /// <summary>
        /// The store of data records
        /// </summary>
        private IDataStore<FHSyncDataRecord<T>> dataRecords;
        /// <summary>
        /// Should the sync be stopped
        /// </summary>
        private static ILogService logger = ServiceFinder.Resolve<ILogService>();
        private static INetworkService networkService = ServiceFinder.Resolve<INetworkService>();

        /// <summary>
        /// The sync configuration
        /// </summary>
        public FHSyncConfig SyncConfig { set; get; }

        /// <summary>
        /// The hash value of the dataset
        /// </summary>
        public String HashValue { get; set; }

        /// <summary>
        /// The id of the data set the sync client is currently managing
        /// </summary>
        [JsonProperty]
        protected string DatasetId { get; set; }

        /// <summary>
        /// When the last sync started
        /// </summary>
        [JsonProperty]
        private Nullable<DateTime> SyncStart { get; set; }

        /// <summary>
        /// When the last sync ended
        /// </summary>
        [JsonProperty]
        private Nullable<DateTime> SyncEnd { get; set; }

        /// <summary>
        /// The query params for the data records. Will be used to send to the cloud when listing initial records.
        /// </summary>
        [JsonProperty("QueryParams")]
        public IDictionary<string, string> QueryParams { get; set; }

        /// <summary>
        /// The meta data for the dataset
        /// </summary>
        [JsonProperty("MetaData")]
        protected FHSyncMetaData MetaData { get; set; }

        /// <summary>
        /// If this is set to true, a sync loop will start almost immediately
        /// </summary>
        /// <value><c>true</c> if force sync; otherwise, <c>false</c>.</value>
        [JsonIgnore]
        public Boolean ForceSync { set; get; }

        /// <summary>
        /// Records change acknowledgements
        /// </summary>
        [JsonProperty("Acknowledgements")]
        protected List<FHSyncResponseUpdatesData> Acknowledgements { get; set; }

        public event EventHandler<FHSyncNotificationEventArgs> SyncNotificationHandler;

        public FHSyncDataset()
        {
        }

        /// <summary>
        /// Init a sync dataset with some parameters
        /// </summary>
        /// <param name="datasetId">Dataset identifier.</param>
        /// <param name="syncConfig">Sync config.</param>
        /// <param name="qp">Query parameters that will be send to the cloud when listing dataset</param>
        /// <param name="meta">Meta data that will be send to the cloud when syncing </param>
        /// <typeparam name="X">The 1st type parameter.</typeparam>
        public static FHSyncDataset<X> Build<X>(string datasetId, FHSyncConfig syncConfig, IDictionary<string, string> qp, FHSyncMetaData meta) where X : IFHSyncModel
        {
            //check if there is a dataset model file exists and load it
            string syncClientMeta = FHSyncUtils.GetDataFilePath(datasetId, PERSIST_FILE_NAME);
            FHSyncDataset<X> dataset = LoadExistingDataSet<X>(syncClientMeta, datasetId);
            if (null == dataset)
            {
                //no existing one, create a new one
                dataset = new FHSyncDataset<X>();
                dataset.DatasetId = datasetId;
                dataset.SyncConfig = syncConfig;
                dataset.QueryParams = null == qp ? new Dictionary<string, string>() : qp;
                dataset.MetaData = null == meta ? new FHSyncMetaData() : meta;
                dataset.dataRecords = new InMemoryDataStore<FHSyncDataRecord<X>>();
                dataset.dataRecords.PersistPath = GetPersistFilePathForDataset(syncConfig, datasetId, DATA_PERSIST_FILE_NAME);
                dataset.pendingRecords = new InMemoryDataStore<FHSyncPendingRecord<X>>();
                dataset.pendingRecords.PersistPath = GetPersistFilePathForDataset(syncConfig, datasetId, PENDING_DATA_PERSIST_FILE_NAME);
                //persist the dataset immediately
                dataset.Save();
            }
            return dataset;
        }

        /// <summary>
        /// List data
        /// </summary>
        public List<T> List()
        {
            List<T> results = new List<T>();
            Dictionary<string, FHSyncDataRecord<T>> storedData = this.dataRecords.List();
            foreach (KeyValuePair<string, FHSyncDataRecord<T>> item in storedData)
            {
                FHSyncDataRecord<T> record = item.Value;
                T data = (T)FHSyncUtils.Clone(record.Data);
                data.UID = item.Key;
                results.Add(data);
            }
            return results;
        }

        /// <summary>
        /// Read data specified by uid.
        /// </summary>
        /// <param name="uid">Uid.</param>
        public T Read(string uid)
        {
            Contract.Assert(null != uid, "uid is null");
            FHSyncDataRecord<T> record = this.dataRecords.Get(uid);
            if (null != record)
            {
                T data = (T)FHSyncUtils.Clone(record.Data);
                data.UID = record.Uid;
                return data;
            }
            else
            {
                return default(T);
            }
        }

        /// <summary>
        /// Create data
        /// </summary>
        /// <param name="data">Data.</param>
        public T Create(T data)
        {
            Contract.Assert(data.UID == null, "data is not new");
            T ret = default(T);
            FHSyncPendingRecord<T> pendingRecord = AddPendingRecord(data, "create");
            if (null != pendingRecord)
            {
                //for creation, the uid will be the uid of the pending record temporarily
                FHSyncDataRecord<T> record = this.dataRecords.Get(pendingRecord.Uid);
                if (null != record)
                {
                    ret = (T)FHSyncUtils.Clone(record.Data);
                    ret.UID = record.Uid;
                }

            }
            if (ret == null)
            {
                throw new Exception("create failed");
            }
            else
            {
                return ret;
            }
        }

        /// <summary>
        /// Update the specified data.
        /// </summary>
        /// <param name="data">Data.</param>
        public T Update(T data)
        {
            Contract.Assert(data.UID != null, "data is new");
            FHSyncDataRecord<T> record = this.dataRecords.Get(data.UID);
            Contract.Assert(null != record, "data record with uid " + data.UID + " doesn't exist");
            T ret = default(T);
            FHSyncPendingRecord<T> pendingRecord = AddPendingRecord(data, "update");
            if (null != pendingRecord)
            {
                FHSyncDataRecord<T> updatedRecord = this.dataRecords.Get(data.UID);
                if (null != updatedRecord)
                {
                    ret = (T)FHSyncUtils.Clone(record.Data);
                    ret.UID = record.Uid;
                }
            }
            if (ret == null)
            {
                throw new Exception("update failed");
            }
            else
            {
                return ret;
            }
        }

        /// <summary>
        /// Delete the specified uid.
        /// </summary>
        /// <param name="uid">Uid.</param>
        public T Delete(string uid)
        {
            Contract.Assert(null != uid, "uid is null");
            FHSyncDataRecord<T> record = this.dataRecords.Get(uid);
            Contract.Assert(null != record, "data record with uid " + uid + " doesn't exist");
            T ret = default(T);
            FHSyncPendingRecord<T> pendingRecord = AddPendingRecord(record.Data, "delete");
            if (null != pendingRecord)
            {
                ret = (T)FHSyncUtils.Clone(record.Data);
                ret.UID = uid;
            }
            if (ret.Equals(default(T)))
            {
                throw new Exception("delete failed");
            }
            else
            {
                return ret;
            }
        }

        protected FHSyncPendingRecord<T> AddPendingRecord(T dataRecords, string action)
        {
            if (!networkService.IsOnline())
            {
                this.OnSyncNotification(dataRecords.UID, SyncNotification.OFFLINE_UPDATE, action);
            }
            //create pendingRecord
            FHSyncPendingRecord<T> pendingRecord = new FHSyncPendingRecord<T>();
            pendingRecord.InFlight = false;
            pendingRecord.Action = action;
            FHSyncDataRecord<T> dataRecord = null;
            if (null != dataRecords)
            {
                dataRecord = new FHSyncDataRecord<T>(dataRecords);
                pendingRecord.PostData = dataRecord;
            }
            if ("create".Equals(action))
            {
                pendingRecord.Uid = pendingRecord.PostData.HashValue;
                dataRecord.Uid = pendingRecord.Uid;
            }
            else
            {
                FHSyncDataRecord<T> existing = this.dataRecords.Get(dataRecords.UID);
                dataRecord.Uid = existing.Uid;
                pendingRecord.Uid = existing.Uid;
                pendingRecord.PreData = existing.Clone();
            }
            StorePendingRecord(pendingRecord);
            string uid = pendingRecord.Uid;
            if("delete".Equals(action)){
                this.dataRecords.Delete(dataRecords.UID);
            } else {
                this.dataRecords.Insert(uid, dataRecord);
            }
            this.Save();
            this.OnSyncNotification(uid, SyncNotification.LOCAL_UPDATE_APPLIED, pendingRecord.Action);
            return pendingRecord;
        }

        //TODO: probably move this to a dedicated PendingRecordsManager
        protected void StorePendingRecord(FHSyncPendingRecord<T> pendingRecord)
        {
            this.pendingRecords.Insert(pendingRecord.GetHashValue(), pendingRecord);
            string previousPendingUID = null;
            FHSyncPendingRecord<T> previousPending = null;
            string uid = pendingRecord.Uid;
            DebugLog("update local dataset for uid " + uid + " - action = " + pendingRecord.Action);
            FHSyncDataRecord<T> existing = dataRecords.Get(uid);
            Boolean fromPending = this.MetaData.GetMetaDataAsBool(uid, "fromPending");
            if ("create".Equals(pendingRecord.Action)) {
                if (null != existing) {
                    DebugLog("data already exists for uid for create :: " + existing.ToString());
                    if (fromPending) {
                        previousPendingUID = this.MetaData.GetMetaDataAsString(uid, "pendingUid");
                        if (null != previousPendingUID) {
                            this.pendingRecords.Delete(previousPendingUID);
                        }
                    }
                }
                this.MetaData.InsertBoolMetaData(uid, "fromPending", true);
                this.MetaData.InsertStringMetaData(uid, "pendingUid", pendingRecord.GetHashValue());
            }

            if ("update".Equals(pendingRecord.Action)) {
                string metaPendingHash = pendingRecord.GetHashValue();
                if (null != existing) {
                    DebugLog("Update an existing pending record for dataset :: " + existing.ToString());
                    previousPendingUID = this.MetaData.GetMetaDataAsString(uid, "pendingUid");
                    if(null != previousPendingUID){
                        this.MetaData.InsertStringMetaData(uid, "previousPendingUid", previousPendingUID);
                        previousPending = this.pendingRecords.Get(previousPendingUID);
                        if(null != previousPending) {
                            if(!previousPending.InFlight) {
                                DebugLog("existing pre-flight pending record =" + previousPending.ToString());
                                previousPending.PostData = pendingRecord.PostData;
                                pendingRecords.Delete(pendingRecord.GetHashValue());
                                metaPendingHash = previousPendingUID;
                            } else {
                                DebugLog("existing in-flight pending record = " + previousPending.ToString());
                                pendingRecord.SetDelayed(previousPending.GetHashValue());
                            }
                        }
                    }
                }
                this.MetaData.InsertBoolMetaData(uid, "fromPending", true);
                this.MetaData.InsertStringMetaData(uid, "pendingUid", metaPendingHash);
            }

            if("delete".Equals(pendingRecord.Action)){
                if(null != existing){
                    if(fromPending){
                        DebugLog("Deleting an existing pending record for dataset :: " + existing.ToString());
                        previousPendingUID = this.MetaData.GetMetaDataAsString(uid, "pendingUid");
                        if(null != previousPendingUID){
                            this.MetaData.InsertStringMetaData(uid, "previousPendingUid", previousPendingUID);
                            previousPending = this.pendingRecords.Get(previousPendingUID);
                            if(!previousPending.InFlight){
                                DebugLog("existing pending record = " + previousPending.ToString());
                                if("create".Equals(previousPending.Action)){
                                    this.pendingRecords.Delete(pendingRecord.GetHashValue());
                                    this.pendingRecords.Delete(previousPendingUID);
                                }
                                if("update".Equals(previousPending.Action)){
                                    pendingRecord.PreData = previousPending.PreData;
                                    pendingRecord.InFlight = false;
                                    this.pendingRecords.Delete(previousPendingUID);
                                }
                            } else {
                                DebugLog("existing in-flight pending record = " + previousPending.ToString());
                                pendingRecord.SetDelayed(previousPending.GetHashValue());
                            }
                        }

                    }
                }
            }

            if(this.SyncConfig.AutoSyncLocalUpdates){
                this.syncPending = true;
            }
        }

        public async Task StartSyncLoop()
        {
            this.syncPending = false;
            this.syncRunning = true;
            this.SyncStart = DateTime.Now;
            this.OnSyncNotification(null, SyncNotification.SYNC_STARTED, null);
            if(networkService.IsOnline()){
                FHSyncLoopParams syncParams = new FHSyncLoopParams(this);
                if(syncParams.Pendings.Count > 0){
                    logger.i(LOG_TAG, "starting sync loop - global hash = " + this.HashValue + " :: params = " + syncParams.ToString(), null);
                }
                try {
                    FHResponse syncRes = await DoCloudCall(syncParams);
                    if(null == syncRes.Error){
                        FHSyncResponseData<T> returnedSyncData = (FHSyncResponseData<T>)FHSyncUtils.DeserializeObject(syncRes.RawResponse, typeof(FHSyncResponseData<T>));

                        //TODO: it should be possible achieve the same effects using one loop through the pending records, there is no need to loop the pending records 6 times!
                        //e.g. 
                        /**
                         * for each pending in pendingRecords
                         *   check if sync response contains update for the pending
                         *       true => update pending pre data from the syn response
                         *       false => update syn response with the pending record post data
                         *          
                         *   if pending is in flight
                         *     if pending is crashed
                         *       check if there is updates for the crashed record
                         *        true => resole the crash status
                         *        false => keep waiting or give up
                         *   
                         *   if pendingRecord is delayed
                         *     check if sync response contains info about the delay records
                         *       true => resolve delayed status
                         */

                        // Check to see if any new pending records need to be updated to reflect the current state of play.
                        this.UpdatePendingFromNewData(returnedSyncData);

                        // Check to see if any previously crashed inflight records can now be resolved
                        this.UpdateCrashedInFlightFromNewData(returnedSyncData);

                        //Check to see if any delayed pending records can now be set to ready
                        this.UpdateDelayedFromNewData(returnedSyncData);

                        //Check meta data as well to make sure it contains the correct info
                        this.UpdateMetaFromNewData(returnedSyncData);

                        // Update the new dataset with details of any inflight updates which we have not received a response on
                        this.UpdateNewDataFromInFlight(returnedSyncData);

                        // Update the new dataset with details of any pending updates
                        this.UpdateNewDataFromPending(returnedSyncData);

                        if(null != returnedSyncData.Records){
                            this.UpdateLocalDatasetFromRemote(returnedSyncData);
                        }

                        if(null != returnedSyncData.Updates){
                            this.ProcessUpdatesFromRemote(returnedSyncData);
                        }

                        if(null == returnedSyncData.Records && returnedSyncData.Hash != null){
                            DebugLog("Local dataset stale - syncing records :: local hash = " + this.HashValue + " - remoteHash = " + returnedSyncData.Hash);
                            //Different hash value returned - sync individual records
                            await this.SyncRecords();
                        } else {
                            DebugLog("Local dataset up to date");
                            this.SyncLoopComplete("online", SyncNotification.SYNC_COMPLETED);
                        }
                    } else {
                        // The HTTP call failed to complete succesfully, so the state of the current pending updates is unknown
                        // Mark them as "crashed". The next time a syncLoop completets successfully, we will review the crashed
                        // records to see if we can determine their current state.
                        this.MarkInFlightAsCrased();
                        DebugLog("syncLoop failed :: res = " + syncRes.RawResponse + " err = " + syncRes.Error);
                        this.SyncLoopComplete(syncRes.RawResponse, SyncNotification.SYNC_FAILED);

                    }
                } catch (Exception e) {
                    DebugLog("Error performing sync - " + e.ToString());
                    this.SyncLoopComplete(e.Message, SyncNotification.SYNC_FAILED);
                }     
            } else {
                this.OnSyncNotification(null, SyncNotification.SYNC_FAILED, "offline");
            }
        }

        private async Task SyncRecords()
        {
            FHSyncRecordsParams syncParams = new FHSyncRecordsParams(this);
            FHResponse syncRecordsRes = await this.DoCloudCall(syncParams);
            if(null == syncRecordsRes.Error){
                FHSyncRecordsResponseData<T> remoteDataRecords = (FHSyncRecordsResponseData<T>) FHSyncUtils.DeserializeObject(syncRecordsRes.RawResponse, typeof(FHSyncRecordsResponseData<T>));

                Dictionary<string, FHSyncDataRecord<T>> createdRecords = remoteDataRecords.CreatedRecords;
                foreach(var created in createdRecords){
                    FHSyncDataRecord<T> r = created.Value;
                    r.Uid = created.Key;
                    this.dataRecords.Insert(created.Key, r);
                    this.OnSyncNotification(created.Key, SyncNotification.RECORD_DELTA_RECEIVED, "create");
                }

                Dictionary<string, FHSyncDataRecord<T>> updatedRecords = remoteDataRecords.UpdatedRecords;
                foreach(var updated in updatedRecords){
                    FHSyncDataRecord<T> r = updated.Value;
                    r.Uid = updated.Key;
                    this.dataRecords.Insert(updated.Key, r);
                    this.OnSyncNotification(updated.Key, SyncNotification.RECORD_DELTA_RECEIVED, "update");
                }

                Dictionary<string, FHSyncDataRecord<T>> deletedRecords = remoteDataRecords.DeletedRecords;
                foreach (var deleted in deletedRecords) {
                    this.dataRecords.Delete(deleted.Key);
                    this.OnSyncNotification(deleted.Key, SyncNotification.RECORD_DELTA_RECEIVED, "delete");    
                }


                this.OnSyncNotification(remoteDataRecords.Hash, SyncNotification.DELTA_RECEIVED, "partial dataset");
                if(null != remoteDataRecords.Hash){
                    this.HashValue = remoteDataRecords.Hash;
                }

                this.SyncLoopComplete("online", SyncNotification.SYNC_COMPLETED);
            } else {
                DebugLog("SyncRecords failed : " + syncRecordsRes.RawResponse + " error = " + syncRecordsRes.Error);
                this.SyncLoopComplete(syncRecordsRes.RawResponse, SyncNotification.SYNC_FAILED);
            }

        }

        private void SyncLoopComplete(string message, SyncNotification notification)
        {
            this.syncRunning = false;
            this.SyncEnd = DateTime.Now;
            this.Save();
            this.OnSyncNotification(this.HashValue, notification, message);
        }

        private void UpdatePendingFromNewData(FHSyncResponseData<T> syncResData)
        {
            if(null != pendingRecords && null != syncResData.Records){
                Dictionary<string, FHSyncPendingRecord<T>> localPendingRecords = pendingRecords.List();
                foreach (var item in localPendingRecords)
                {
                    FHSyncPendingRecord<T> pendingRecord = item.Value;
                    if(!pendingRecord.InFlight){
                        //process pending records that have not been submitted
                        DebugLog("Found Non in flight record -> action = " + pendingRecord.Action + " :: uid=" + pendingRecord.Uid + " :: hash=" + pendingRecord.GetHashValue());
                        if("update".Equals(pendingRecord.Action) || "delete".Equals(pendingRecord.Action)){
                            //update the prevalue of pending record to reflect the latest data returned from sync
                            //This will prevent a collision being reported when the pending record is sent
                            //TODO: is this mean we are blindly apply changes from remote to the current store, then when the local change is submitted, the remote data will be overridden by local updates even local updates could be wrong?
                            FHSyncDataRecord<T> returnedRecord = null;
                            syncResData.Records.TryGetValue(pendingRecord.Uid, out returnedRecord);
                            if(null != returnedRecord){
                                DebugLog("updating pre values for existing pending record " + pendingRecord.Uid);
                                pendingRecord.PreData = returnedRecord;
                            } else {
                                //The update/delete maybe for a newly created record in which case the uid will have changed
                                string previousPendingUid = this.MetaData.GetMetaDataAsString(pendingRecord.Uid, "previousPendingUid");
                                if(null != previousPendingUid){
                                    FHSyncPendingRecord<T> previousPendingRecord = null;
                                    localPendingRecords.TryGetValue(previousPendingUid, out previousPendingRecord);
                                    if(null != previousPendingRecord){
                                        FHSyncResponseUpdatesData appliedRecord = syncResData.GetAppliedUpdates(previousPendingRecord.GetHashValue());
                                        if(null != appliedRecord){
                                            string newUid = appliedRecord.Uid;
                                            FHSyncDataRecord<T> newRecord = syncResData.GetRemoteRecord(newUid);
                                            if(null != newRecord){
                                                DebugLog("Updating pre values for existing pending record which was previously a create " + pendingRecord.Uid + " => " + newUid);
                                                pendingRecord.PreData = newRecord;
                                                pendingRecord.Uid = newUid;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if("create".Equals(pendingRecord.Action)){
                            FHSyncResponseUpdatesData appliedRecord = syncResData.GetAppliedUpdates(pendingRecord.GetHashValue());
                            if(null != appliedRecord){
                                DebugLog("Found an update for a pending create + " + appliedRecord.ToString());
                                FHSyncDataRecord<T> newRecord = syncResData.GetRemoteRecord(pendingRecord.GetHashValue());
                                if(null != newRecord){
                                    DebugLog("Changing pending create to an update based on new record " + newRecord.ToString());
                                    pendingRecord.Action = "update";
                                    pendingRecord.PreData = newRecord;
                                    pendingRecord.Uid = appliedRecord.Uid;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void UpdateCrashedInFlightFromNewData(FHSyncResponseData<T> syncResData)
        {
            Dictionary<string, FHSyncPendingRecord<T>> localPendingRecords = this.pendingRecords.List();

            foreach (string pendingRecordKey in localPendingRecords.Keys)
            {
                bool processed = false;
                FHSyncPendingRecord<T> pendingRecord = localPendingRecords[pendingRecordKey];
                if(pendingRecord.InFlight && pendingRecord.Crashed){
                    DebugLog("Found crashed inFlight pending record uid =" + pendingRecord.Uid + " :: hash = " + pendingRecord.GetHashValue());
                    if(null != syncResData.Updates && syncResData.Updates.ContainsKey("hashes") ){
                        FHSyncResponseUpdatesData crashedUpdate = syncResData.GetUpdateByHash(pendingRecord.GetHashValue());
                        if(null != crashedUpdate){
                            DebugLog("resolving status for crashed inflight pending record " + crashedUpdate.ToString());
                            if(crashedUpdate.Type == FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.failed){
                                if(crashedUpdate.Action.Equals("create")){
                                    DebugLog("Deleting failed create from dataset");
                                    this.dataRecords.Delete(crashedUpdate.Uid);
                                } else if(crashedUpdate.Action.Equals("update") || crashedUpdate.Action.Equals("delete")){
                                    DebugLog("Reverting failed " + crashedUpdate.Action + " in dataset");
                                    this.dataRecords.Insert(crashedUpdate.Uid, pendingRecord.PreData);
                                }
                            }

                            this.pendingRecords.Delete(pendingRecordKey);
                            switch (crashedUpdate.Type)
                            {
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.applied:
                                    OnSyncNotification(crashedUpdate.Uid, SyncNotification.REMOTE_UPDATE_APPLIED, crashedUpdate.ToString());
                                    break;
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.failed:
                                    OnSyncNotification(crashedUpdate.Uid, SyncNotification.REMOTE_UPDATE_FAILED, crashedUpdate.ToString());
                                    break;
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.collisions:
                                    OnSyncNotification(crashedUpdate.Uid, SyncNotification.COLLISION_DETECTED, crashedUpdate.ToString());
                                    break;
                                default:
                                    break;
                            }
                            processed = true;
                        }
                    }
                    if(!processed){
                        //no word on our crashed upate - increment a counter to reflect another sync that did not give us any updates on our crashed record
                        pendingRecord.IncrementCrashCount();
                        if(pendingRecord.CrashedCount > this.SyncConfig.CrashedCountWait){
                            DebugLog("Crashed inflight pending record has reached CrashedCount limit");
                            if(this.SyncConfig.ResendCrashedUpdated){
                                DebugLog("Retrying crashed inflight pending record");
                                pendingRecord.ResetCrashStatus();
                            } else {
                                DebugLog("Deleting crashed inflight pending record");
                                this.pendingRecords.Delete(pendingRecordKey);
                            }
                        }
                    } 
                }
            }
        }

        private void UpdateDelayedFromNewData(FHSyncResponseData<T> syncResData){
            Dictionary<string, FHSyncPendingRecord<T>> localPendingRecords = this.pendingRecords.List();
            foreach (string pendingRecordKey in localPendingRecords.Keys)
            {
                FHSyncPendingRecord<T> pendingRecord = localPendingRecords[pendingRecordKey];
                if(pendingRecord.Delayed && null != pendingRecord.Waiting){
                    DebugLog("Found delayed pending record uid = " + pendingRecord.Uid + " :: hash=" + pendingRecord.GetHashValue());
                    FHSyncResponseUpdatesData waitingRecord = syncResData.GetUpdateByHash(pendingRecord.Waiting);
                    if(null != waitingRecord){
                        DebugLog("Waiting pending record is resolved rec =" + waitingRecord.ToString());
                        pendingRecord.ResetDelayed();
                    }
                }
            }
        }

        private void UpdateMetaFromNewData(FHSyncResponseData<T> syncResData)
        {
            FHSyncMetaData metaData = this.MetaData;
            Dictionary<string, Dictionary<string, string>>.KeyCollection keys = this.MetaData.Keys;
            List<string> keysToDelete = new List<string>();
            foreach (string metaDataKey in keys)
            {
                string pendingHash = metaData.GetMetaDataAsString(metaDataKey, "pendingUid");
                string previousPendingHash = metaData.GetMetaDataAsString(metaDataKey, "previousPendingUid");
                DebugLog("Found metadata with uid = " + metaDataKey + " :: pendingHash = " + pendingHash + " :: previousPendingHash " + previousPendingHash);
                bool previousPendingResolved = true;
                bool pendingResolved = true;
                if(null != previousPendingHash){
                    //we have previous pending in meta data, see if it's resolved
                    previousPendingResolved = false;
                    FHSyncResponseUpdatesData updateFromRes = syncResData.GetUpdateByHash(previousPendingHash);
                    if(null != updateFromRes){
                        DebugLog("Found previousPendingUid in meta data resolved - resolved = " + updateFromRes.ToString());
                        //the previous pending is resolved in the cloud
                        metaData.DeleteMetaData(metaDataKey, "previousPendingUid");
                        previousPendingResolved = true;
                    }
                }
                if(null != pendingHash){
                    //we have current pending in meta data, see if it's resolved
                    pendingResolved = false;
                    FHSyncResponseUpdatesData updateFromRes = syncResData.GetUpdateByHash(pendingHash);
                    if(null != updateFromRes){
                        DebugLog("Found pendingUid in meta data resolved - resolved = " + updateFromRes.ToString());
                        //the current pending is resolved in the cloud
                        metaData.DeleteMetaData(metaDataKey, "pendingUid");
                        pendingResolved = true;
                    }

                }

                if(pendingResolved && previousPendingResolved){
                    DebugLog("both previous and current pendings are resolved for meta data with uid " + metaDataKey + ". Delete it");

                    keysToDelete.Add(metaDataKey);
                }
            }

            foreach (string keyToDelete in keysToDelete)
            {
                this.MetaData.Delete(keyToDelete);
            }
        }

        private void UpdateNewDataFromInFlight(FHSyncResponseData<T> syncResData)
        {
            if(null != syncResData.Records){
                Dictionary<string, FHSyncPendingRecord<T>> localPendingRecords = this.pendingRecords.List();
                foreach (string pendingRecordKey in localPendingRecords.Keys)
                {
                    FHSyncPendingRecord<T> pendingRecord = localPendingRecords[pendingRecordKey];
                    if(pendingRecord.InFlight){
                        FHSyncResponseUpdatesData updatedPending = syncResData.GetUpdateByHash(pendingRecordKey);
                        if(null == updatedPending){
                            DebugLog("Found inFlight pending record -> action =" + pendingRecord.Action + " :: uid = " + pendingRecord.Uid + " :: hash = " + pendingRecord.GetHashValue());
                            FHSyncDataRecord<T> newRecord = syncResData.GetRemoteRecord(pendingRecord.Uid);
                            if(pendingRecord.Action.Equals("update") && null != newRecord){
                                newRecord = pendingRecord.PostData;
                            } else if(pendingRecord.Action.Equals("delete") && null != newRecord){
                                syncResData.Records.Remove(pendingRecord.Uid);
                            } else if(pendingRecord.Action.Equals("create")){
                                DebugLog("re adding pending create to incomming dataset");
                                FHSyncDataRecord<T> createRecordData = pendingRecord.PostData.Clone();
                                syncResData.Records[pendingRecord.Uid] = createRecordData;
                            }
                        }
                    }    
                }
            }
        }

        private void UpdateNewDataFromPending(FHSyncResponseData<T> syncResData)
        {
            if(null != syncResData.Records){
                Dictionary<string, FHSyncPendingRecord<T>> localPendingRecords = this.pendingRecords.List();
                foreach (string pendingRecordKey in localPendingRecords.Keys)
                {
                    FHSyncPendingRecord<T> pendingRecord = localPendingRecords[pendingRecordKey];
                    if(!pendingRecord.InFlight){
                        DebugLog("Found non inFlight record -> action =" + pendingRecord.Action + " :: uid = " + pendingRecord.Uid + " :: hash = " + pendingRecord.GetHashValue());
                        FHSyncDataRecord<T> newRecord = syncResData.GetRemoteRecord(pendingRecord.Uid);
                        if(pendingRecord.Action.Equals("update") && null != newRecord){
                            newRecord = pendingRecord.PostData;
                        } else if(pendingRecord.Action.Equals("delete") && null != newRecord){
                            syncResData.Records.Remove(pendingRecord.Uid);
                        } else if(pendingRecord.Action.Equals("create")){
                            DebugLog("re adding pending create to incomming dataset");
                            FHSyncDataRecord<T> createRecordData = pendingRecord.PostData.Clone();
                            syncResData.Records[pendingRecord.Uid] = createRecordData;
                        }
                    }    
                }
            }
        }

        private void UpdateLocalDatasetFromRemote(FHSyncResponseData<T> syncResData)
        {
            IDataStore<FHSyncDataRecord<T>> anotherDataStore = new InMemoryDataStore<FHSyncDataRecord<T>>();
            foreach(var item in syncResData.Records){
                FHSyncDataRecord<T> record = item.Value;
                record.Uid = item.Key;
                anotherDataStore.Insert(item.Key, record);
            }
            anotherDataStore.PersistPath = this.dataRecords.PersistPath;
            this.dataRecords = anotherDataStore;
            this.HashValue = syncResData.Hash;
            this.OnSyncNotification(syncResData.Hash, SyncNotification.DELTA_RECEIVED, "full dataset");
        }

        private void ProcessUpdatesFromRemote(FHSyncResponseData<T> syncResData)
        {
            List<FHSyncResponseUpdatesData> acks = new List<FHSyncResponseUpdatesData>();
            foreach(string key in syncResData.Updates.Keys){
                if(!"hashes".Equals(key)){
                    Dictionary<string, FHSyncResponseUpdatesData> updates = syncResData.Updates[key];
                    foreach(var item in updates){
                        SyncNotification notification = default(SyncNotification);
                        FHSyncResponseUpdatesData update = item.Value;
                        acks.Add(update);
                        FHSyncPendingRecord<T> pending = this.pendingRecords.Get(item.Key);
                        if(null != pending && pending.InFlight && !pending.Crashed){
                            this.pendingRecords.Delete(item.Key);
                            switch (update.Type)
                            {
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.applied:
                                    notification = SyncNotification.REMOTE_UPDATE_APPLIED;
                                    break;
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.failed:
                                    notification = SyncNotification.REMOTE_UPDATE_FAILED;
                                    break;
                                case FHSyncResponseUpdatesData.FHSyncResponseUpdatesDataType.collisions:
                                    notification = SyncNotification.COLLISION_DETECTED;
                                    break;
                                default:
                                    break;
                            }
                            this.OnSyncNotification(update.Uid, notification, update.ToString());
                        }
                    }
                }
            }
            this.Acknowledgements = acks;
        }


        private void MarkInFlightAsCrased(){
            foreach (var item in this.pendingRecords.List())
            {
                FHSyncPendingRecord<T> pendingRecord = item.Value;
                if(pendingRecord.InFlight){
                    DebugLog("Marking in flight pending record as crashed : " + item.Key);
                    pendingRecord.Crashed = true;
                }
            }
        }

        private void DebugLog(string message, [CallerMemberName] string methodName = "")
        {
            string logMessage = string.Format("{0} - {1}", methodName, message);
            logger.d(LOG_TAG, logMessage, null);
        }

        private async Task<FHResponse> DoCloudCall(object syncParams)
        {
            if(this.SyncConfig.SyncCloud == FHSyncConfig.SyncCloudType.AUTO){
                await CheckSyncCloudType();
            }

            if(this.SyncConfig.SyncCloud == FHSyncConfig.SyncCloudType.MBBAS) {
                string service = string.Format("sync/{0}", this.DatasetId);
                FHResponse res = await FH.Mbaas(service, syncParams);
                return res;
            } else {
                FHResponse res = await FH.Act(this.DatasetId, syncParams);
                return res;
            }
        }

        private async Task CheckSyncCloudType()
        {
            Dictionary<string, object> actParams = new Dictionary<string, object>();
            actParams.Add("fh", "sync");
            FHResponse actRes = await FH.Act(this.DatasetId, actParams);
            if(actRes.StatusCode == HttpStatusCode.OK || actRes.StatusCode == HttpStatusCode.InternalServerError){
                this.SyncConfig.SyncCloud = FHSyncConfig.SyncCloudType.LEGACY;
            } else {
                this.SyncConfig.SyncCloud = FHSyncConfig.SyncCloudType.MBBAS;
            }
        }




        /// <summary>
        /// Persist the dataset
        /// </summary>
        protected void Save()
        {
            this.dataRecords.Save();
            this.pendingRecords.Save();
            string syncClientMeta = FHSyncUtils.GetDataFilePath(this.DatasetId, PERSIST_FILE_NAME);
            IIOService iosService = ServiceFinder.Resolve<IIOService>();
            string content = FHSyncUtils.SerializeObject(this);
            try
            {
                iosService.WriteFile(syncClientMeta, content);
            }
            catch (Exception ex)
            {
                logger.e(LOG_TAG, "Failed to save dataset", ex);
                throw ex;
            }

        }

        private static FHSyncDataset<X> LoadExistingDataSet<X>(string syncClientMetaFile, string datasetId) where X : IFHSyncModel
        {
            FHSyncDataset<X> result = null;
            IIOService ioService = ServiceFinder.Resolve<IIOService>();
            if (ioService.Exists(syncClientMetaFile))
            {
                string content = ioService.ReadFile(syncClientMetaFile);
                if (!string.IsNullOrEmpty(content))
                {
                    try
                    {
                        FHSyncDataset<X> syncDataset = (FHSyncDataset<X>)FHSyncUtils.DeserializeObject(content, typeof(FHSyncDataset<X>));
                        if (null != syncDataset)
                        {
                            result = LoadDataForDataset<X>(syncDataset);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.d(LOG_TAG, "Failed to load existing dataset", ex);
                        throw ex;
                    }
                }
            }
            return result;
        }

        private static FHSyncDataset<X> LoadDataForDataset<X>(FHSyncDataset<X> dataSet) where X: IFHSyncModel
        {
            string datasetFile = GetPersistFilePathForDataset(dataSet.SyncConfig, dataSet.DatasetId, DATA_PERSIST_FILE_NAME);
            string pendingdatasetFile = GetPersistFilePathForDataset(dataSet.SyncConfig, dataSet.DatasetId, PENDING_DATA_PERSIST_FILE_NAME);
            dataSet.dataRecords = InMemoryDataStore<FHSyncDataRecord<X>>.Load<FHSyncDataRecord<X>>(datasetFile);
            dataSet.pendingRecords = InMemoryDataStore<FHSyncPendingRecord<X>>.Load<FHSyncPendingRecord<X>>(pendingdatasetFile);
            return dataSet;
        }

        private static String GetPersistFilePathForDataset(FHSyncConfig syncConfig, string datasetId, string fileName)
        {
            string filePath = FHSyncUtils.GetDataFilePath(datasetId, fileName);
            if(null != syncConfig){
                if(!string.IsNullOrEmpty(syncConfig.DataPersistanceDir)){
                    filePath = Path.Combine(syncConfig.DataPersistanceDir, datasetId, fileName);
                }
            }
            return filePath;
        }

        /// <summary>
        /// Check if a sync loop should run
        /// </summary>
        /// <returns><c>true</c>, if sync was shoulded, <c>false</c> otherwise.</returns>
        public bool ShouldSync()
        {
           if(!syncRunning && (this.SyncConfig.SyncActive || this.ForceSync)){
                if(this.ForceSync){
                    this.syncPending = true;
                } else if(null == SyncStart){
                    DebugLog(this.DatasetId + " - Performing initial sync");
                    this.syncPending = true;
                } else if(null != SyncEnd){
                    DateTime nextSync = SyncEnd.Value.Add(TimeSpan.FromSeconds(this.SyncConfig.SyncFrequency));
                    if(DateTime.Now >= nextSync){
                        this.syncPending = true;
                    }
                }
                if(this.syncPending){
                    this.ForceSync = false;
                }
                return this.syncPending; 
           } else {
                return false;
           }
        }

        public async void RunSyncLoop()
        {
            DebugLog("Checking if sync loop should run");
            if(this.ShouldSync()){
               await this.StartSyncLoop();
            }
        }

        /// <summary>
        /// Will run a sync loop
        /// </summary>
        public void DoSync()
        {
            this.syncPending = true;
        }

        /// <summary>
        /// Stop the sync
        /// </summary>
        public void StopSync()
        {
            if(this.SyncConfig.SyncActive){
                this.SyncConfig.SyncActive = false;
            }
        }

        /// <summary>
        /// Start sync
        /// </summary>
        public void StartSync()
        {
            if(!this.SyncConfig.SyncActive){
                this.SyncConfig.SyncActive = true;
            }
        }

        public IDataStore<FHSyncPendingRecord<T>> GetPendingRecords()
        {
            return this.pendingRecords.Clone();
        }

        protected virtual void OnSyncNotification(string uid, SyncNotification code, string message)
        {
            if(null != this.SyncNotificationHandler){
                FHSyncNotificationEventArgs args = new FHSyncNotificationEventArgs
                {
                    DatasetId = this.DatasetId,
                    Uid = uid,
                    Code = code,
                    Message = message
                };
                this.SyncNotificationHandler(this, args);
            }
        }
            

        public class FHSyncLoopParams
        {
            public FHSyncLoopParams()
            {

            }

            [JsonProperty("fn")]
            public string FnName { get; set; }
            [JsonProperty("dataset_id")]
            public string DatasetId { get; set; }
            [JsonProperty("query_params")]
            public IDictionary<string, string> QueryParams { set; get; }
            [JsonProperty("config")]
            public FHSyncConfig SyncConfg { get; set; }
            [JsonProperty("meta_data")]
            public FHSyncMetaData MetaData { get; set; }
            [JsonProperty("dataset_hash")]
            public string Hash { set; get; }
            [JsonProperty("acknowledgements")]
            public List<FHSyncResponseUpdatesData> Acknowledgements { set; get;}
            [JsonProperty("pending")]
            public List<JObject> Pendings { set; get; }

            public FHSyncLoopParams(FHSyncDataset<T> dataset)
            {
                this.FnName = "sync";
                this.DatasetId = dataset.DatasetId;
                this.QueryParams = dataset.QueryParams;
                this.SyncConfg = dataset.SyncConfig;
                this.MetaData = dataset.MetaData;
                this.Hash = dataset.HashValue;
                this.Acknowledgements = dataset.Acknowledgements;
                List<JObject> pendingRecords = new List<JObject>();
                foreach (KeyValuePair<string, FHSyncPendingRecord<T>> item in dataset.pendingRecords.List()) {
                    FHSyncPendingRecord<T> record = item.Value;
                    if(!record.InFlight && !record.Crashed && !record.Delayed) {
                        record.InFlight = true;
                        record.InFlightDate = DateTime.Now;
                        pendingRecords.Add(record.AsJObjectWithHash());
                    }
                }
                this.Pendings = pendingRecords;
            }

            public override string ToString()
            {
                return FHSyncUtils.SerializeObject(this);
            }
        }

        public class FHSyncRecordsParams
        {
            public FHSyncRecordsParams()
            {
                
            }

            [JsonProperty("fn")]
            public string FnName { get; set; }
            [JsonProperty("dataset_id")]
            public string DatasetId { get; set; }
            [JsonProperty("query_params")]
            public IDictionary<string, string> QueryParams { set; get; }
            [JsonProperty("clientRecs")]
            Dictionary<string, string> ClientRecords { set; get; }
            [JsonProperty("dataset_hash")]
            public string Hash { set; get; }

            public FHSyncRecordsParams(FHSyncDataset<T> dataset)
            {
                this.FnName = "syncRecords";
                this.DatasetId = dataset.DatasetId;
                this.QueryParams = dataset.QueryParams;
                this.Hash = dataset.HashValue;
                Dictionary<string, string> records = new Dictionary<string, string>();
                foreach(var item in dataset.dataRecords.List()){
                    records.Add(item.Value.Uid, item.Value.HashValue);
                }
                this.ClientRecords = records;
            }

        }
    }

    public class FHSyncMetaData
    {
        public FHSyncMetaData()
        {
            this.metaData = new Dictionary<string, Dictionary<string, string>>();
        }

        public Dictionary<string, Dictionary<string, string>> metaData { set; get; }

        private Dictionary<string, string> GetDict(string uid)
        {
            Dictionary<string, string> dict = null;
            if(metaData.ContainsKey(uid)){
                metaData.TryGetValue(uid, out dict);
            }
            if (null == dict)
                {
                    dict = new Dictionary<string, string>();
                    metaData[uid] = dict;
                }
            return dict;
        }

        public void InsertStringMetaData(string uid, string key, string value)
        {
            GetDict(uid);
            metaData[uid][key] = value;
        }

        public void InsertBoolMetaData(string uid, string key, bool value)
        {
            GetDict(uid);
            metaData[uid][key] = value.ToString();
        }

        public string GetMetaDataAsString(string uid, string key)
        {
            if(metaData.ContainsKey(uid)){
                Dictionary<string, string> dict = GetDict(uid);
                string value = null;
                dict.TryGetValue(key, out value);
                return value;
            } else {
                return null;
            }

        }

        public bool GetMetaDataAsBool(string uid, string key)
        {
            string val = GetMetaDataAsString(uid, key);
            if (null != val)
            {
                return Boolean.Parse(val);
            }
            else
            {
                return false;
            }
        }

        [JsonIgnore]
        public Dictionary<string, Dictionary<string, string>>.KeyCollection Keys 
        {
            get {
                return this.metaData.Keys;
            }
        }

        public void DeleteMetaData(string uid, string key)
        {
            Dictionary<string, string>  dict = GetDict(uid);
            if(dict.ContainsKey(key)){
                dict.Remove(key);
            }
        }

        public void Delete(string uid)
        {
            if(metaData.ContainsKey(uid)){
                metaData.Remove(uid);
            }
        }

    }

    public class FHSyncResponseData<T> where T : IFHSyncModel
    {
        public FHSyncResponseData()
        {

        }

        [JsonProperty("records")]
        public Dictionary<string, FHSyncDataRecord<T>> Records { set; get; }

        [JsonProperty("updates")]
        public Dictionary<string, Dictionary<string, FHSyncResponseUpdatesData>> Updates { set; get; }

        [JsonProperty("hash")]
        public string Hash { set; get; }

        public FHSyncResponseUpdatesData GetAppliedUpdates(string key)
        {
            if(null != this.Updates && this.Updates.Count > 0){
                if(this.Updates.ContainsKey("applied")){
                    Dictionary<string, FHSyncResponseUpdatesData> appliedRecords = this.Updates["applied"];
                    if(appliedRecords.ContainsKey(key)){
                        return appliedRecords[key];
                    }
                }
            }
            return null;
        }

        public FHSyncDataRecord<T> GetRemoteRecord(string key)
        {
            if(null != this.Records && this.Records.Count > 0){
                if(this.Records.ContainsKey(key)){
                    return this.Records[key];
                }
            }
            return null;
        }

        public FHSyncResponseUpdatesData GetUpdateByHash(string hash){
            if(null != this.Updates && this.Updates.ContainsKey("hashes")){
                Dictionary<string, FHSyncResponseUpdatesData> hashes = this.Updates["hashes"];
                if(hashes.ContainsKey(hash)){
                    return hashes[hash];
                }
            }
            return null;
        }
    }

    public class FHSyncResponseUpdatesData
    {
        public FHSyncResponseUpdatesData()
        {

        }

        public enum FHSyncResponseUpdatesDataType
        {
            applied,
            failed,
            collisions
        }

        [JsonProperty("cuid")]
        public string Cuid { set; get; }
        [JsonProperty("type")]
        [JsonConverter(typeof(StringEnumConverter))]
        public FHSyncResponseUpdatesDataType Type { set; get; }
        [JsonProperty("action")]
        public string Action { set; get; }
        [JsonProperty("hash")]
        public string Hash { set; get; }
        [JsonProperty("uid")]
        public string Uid { set; get; }
        [JsonProperty("message")]
        public string Message { set; get; }
    }

    public class FHSyncRecordsResponseData<T> where T: IFHSyncModel
    {
        public FHSyncRecordsResponseData()
        {

        }

        [JsonProperty("hash")]
        public string Hash { set; get; }

        [JsonProperty("create")]
        public Dictionary<string, FHSyncDataRecord<T>> CreatedRecords { set; get; }

        [JsonProperty("update")]
        public Dictionary<string, FHSyncDataRecord<T>> UpdatedRecords { set; get; }

        [JsonProperty("delete")]
        public Dictionary<string, FHSyncDataRecord<T>> DeletedRecords { set; get; }

    }
        
}

