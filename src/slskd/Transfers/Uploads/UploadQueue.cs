﻿// <copyright file="UploadQueue.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Transfers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Serilog;
    using slskd.Users;
    using Soulseek;

    /// <summary>
    ///     Orchestrates uploads.
    /// </summary>
    public interface IUploadQueue
    {
        /// <summary>
        ///     Awaits the start of an upload.
        /// </summary>
        /// <param name="transfer">The upload for which to wait.</param>
        /// <returns>The operation context.</returns>
        Task AwaitStartAsync(Transfer transfer);

        /// <summary>
        ///     Signals the completion of an upload.
        /// </summary>
        /// <param name="transfer">The completed upload.</param>
        void Complete(Transfer transfer);

        /// <summary>
        ///     Enqueues an upload.
        /// </summary>
        /// <param name="transfer">The upload to enqueue.</param>
        void Enqueue(Transfer transfer);

        /// <summary>
        ///     Computes the estimated queue position of the specified <paramref name="username"/> if they were to enqueue a file.
        /// </summary>
        /// <param name="username">The username for which to estimate.</param>
        /// <returns>The estimated queue position if the user were to enqueue a file.</returns>
        int EstimatePosition(string username);

        /// <summary>
        ///     Computes the estimated queue position of the specified <paramref name="filename"/> for the specified <paramref name="username"/>.
        /// </summary>
        /// <param name="username">The username associated with the file.</param>
        /// <param name="filename">The filename of the file for which the position is to be estimated.</param>
        /// <returns>The estimated queue position of the file.</returns>
        /// <exception cref="FileNotFoundException">Thrown if the specified filename is not enqueued.</exception>
        int EstimatePosition(string username, string filename);

        /// <summary>
        ///     Determines whether an upload slot is available for the specified <paramref name="username"/>.
        /// </summary>
        /// <param name="username">The username for which slot availability is to be checked.</param>
        /// <returns>A value indicating whether an upload slot is available for the specified username.</returns>
        bool IsSlotAvailable(string username);
    }

    /// <summary>
    ///     Orchestrates uploads.
    /// </summary>
    public class UploadQueue : IUploadQueue
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="UploadQueue"/> class.
        /// </summary>
        /// <param name="userService">The UserService instance to use.</param>
        /// <param name="optionsMonitor">The OptionsMonitor instance to use.</param>
        public UploadQueue(
            IUserService userService,
            IOptionsMonitor<Options> optionsMonitor)
        {
            Users = userService;

            OptionsMonitor = optionsMonitor;
            OptionsMonitor.OnChange(Configure);

            Configure(OptionsMonitor.CurrentValue);
        }

        private Dictionary<string, Group> Groups { get; set; } = new Dictionary<string, Group>();
        private int LastGlobalSlots { get; set; }
        private string LastOptionsHash { get; set; }
        private ILogger Log { get; } = Serilog.Log.ForContext<UploadQueue>();
        private int MaxSlots { get; set; } = 0;
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private SemaphoreSlim SyncRoot { get; } = new SemaphoreSlim(1, 1);
        private ConcurrentDictionary<string, List<Upload>> Uploads { get; set; } = new ConcurrentDictionary<string, List<Upload>>();
        private IUserService Users { get; }

        /// <summary>
        ///     Awaits the start of an upload.
        /// </summary>
        /// <param name="transfer">The upload for which to wait.</param>
        /// <returns>The operation context.</returns>
        public Task AwaitStartAsync(Transfer transfer)
        {
            SyncRoot.Wait();

            try
            {
                if (!Uploads.TryGetValue(transfer.Username, out var list))
                {
                    throw new SlskdException($"No enqueued uploads for user {transfer.Username}");
                }

                var upload = list.FirstOrDefault(e => e.Filename == transfer.Filename);

                if (upload == default)
                {
                    throw new SlskdException($"File {transfer.Filename} is not enqueued for user {transfer.Username}");
                }

                upload.Ready = DateTime.UtcNow;
                Log.Debug("Ready: {File} for {User} at {Time}", Path.GetFileName(upload.Filename), upload.Username, upload.Enqueued);

                return upload.TaskCompletionSource.Task;
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        /// <summary>
        ///     Signals the completion of an upload.
        /// </summary>
        /// <param name="transfer">The completed upload.</param>
        public void Complete(Transfer transfer)
        {
            SyncRoot.Wait();

            try
            {
                if (!Uploads.TryGetValue(transfer.Username, out var list))
                {
                    throw new SlskdException($"No enqueued uploads for user {transfer.Username}");
                }

                var upload = list.FirstOrDefault(e => e.Filename == transfer.Filename);

                if (upload == default)
                {
                    throw new SlskdException($"File {transfer.Filename} is not enqueued for user {transfer.Username}");
                }

                list.Remove(upload);
                Log.Debug("Complete: {File} for {User} at {Time}", Path.GetFileName(upload.Filename), upload.Username, upload.Enqueued);

                // ensure the slot is returned to the group from which it was acquired the group may have been removed during the
                // transfer. if so, do nothing.
                if (Groups.ContainsKey(upload.Group ?? string.Empty))
                {
                    var group = Groups[upload.Group];

                    group.UsedSlots = Math.Max(0, group.UsedSlots - 1);
                    Log.Debug("Group {Group} slots: {Used}/{Available}", group.Name, group.UsedSlots, group.Slots);
                }

                if (!list.Any() && Uploads.TryRemove(transfer.Username, out _))
                {
                    Log.Debug("Cleaned up tracking list for {User}; no more queued uploads to track", transfer.Username);
                }
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        /// <summary>
        ///     Enqueues an upload.
        /// </summary>
        /// <param name="transfer">The upload to enqueue.</param>
        public void Enqueue(Transfer transfer)
        {
            SyncRoot.Wait();

            try
            {
                var upload = new Upload() { Username = transfer.Username, Filename = transfer.Filename };

                Uploads.AddOrUpdate(
                    key: transfer.Username,
                    addValue: new List<Upload>(new[] { upload }),
                    updateValueFactory: (key, list) =>
                    {
                        list.Add(upload);
                        return list;
                    });

                Log.Debug("Enqueued: {File} for {User} at {Time}", Path.GetFileName(upload.Filename), upload.Username, upload.Enqueued);
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        /// <summary>
        ///     Computes the estimated queue position of the specified <paramref name="username"/> if they were to enqueue a file.
        /// </summary>
        /// <param name="username">The username for which to estimate.</param>
        /// <returns>The estimated queue position if the user were to enqueue a file.</returns>
        public int EstimatePosition(string username)
        {
            if (IsSlotAvailable(username))
            {
                return 0;
            }

            var group = Users.GetGroup(username);
            return Uploads.GetValueOrDefault(group)?.Count ?? 0;
        }

        /// <summary>
        ///     Computes the estimated queue position of the specified <paramref name="filename"/> for the specified <paramref name="username"/>.
        /// </summary>
        /// <param name="username">The username associated with the file.</param>
        /// <param name="filename">The filename of the file for which the position is to be estimated.</param>
        /// <returns>The estimated queue position of the file.</returns>
        /// <exception cref="FileNotFoundException">Thrown if the specified filename is not enqueued.</exception>
        public int EstimatePosition(string username, string filename)
        {
            var groupName = Users.GetGroup(username);
            var groupRecord = Groups.GetValueOrDefault(groupName);

            // the Uploads dictionary is keyed by username; gather all of the users that belong to the same group as the requested user
            var uploadsForGroup = Uploads.Where(kvp => Users.GetGroup(kvp.Key) == groupName);

            // the RoundRobin queue implementation is not strictly fair to all users; only uploads that are ready are candidates
            // for selection. this means that if Bob downloads files twice as fast as Alice, Bob is going to advance through the
            // queue twice as fast, too. assume everyone downloads at equal speed for this estimate. also assume that all files
            // are of equal length.
            if (groupRecord.Strategy == QueueStrategy.RoundRobin)
            {
                // find this user's uploads
                var uploadsForUser = uploadsForGroup.FirstOrDefault(group => group.Key == username).Value;

                if (uploadsForUser == null || !uploadsForUser.Any())
                {
                    throw new FileNotFoundException($"File {filename} is not enqueued for user {username}");
                }

                // find the position of the requested file in the user's queue
                var localPosition = uploadsForUser.FindIndex(upload => upload.Username == username && upload.Filename == filename);

                if (localPosition < 0)
                {
                    throw new FileNotFoundException($"File {filename} is not enqueued for user {username}");
                }

                // start the position to the local position within this user's queue; the user's own files must be completed
                // before this one can start.
                var position = localPosition;

                // for each other user, add either localPosition or the count of that user's uploads, whichever is less
                foreach (var group in uploadsForGroup.Where(group => group.Key != username))
                {
                    position += Math.Min(localPosition, group.Value.Count);
                }

                return position;
            }

            // for FIFO queues, files are uploaded in the order they are enqueued, so the position should be pretty good estimate.
            // List ordering is guaranteed, so we are getting an accurate portrayal of where this file is in the queue by order of
            // time enqueued. this includes uploads that are in progress.
            var flattenedSortedUploadsForGroup = uploadsForGroup
                .SelectMany(group => group.Value)
                .OrderBy(upload => upload.Enqueued)
                .ToList();

            var globalPosition = flattenedSortedUploadsForGroup.FindIndex(upload => upload.Username == username && upload.Filename == filename);

            if (globalPosition < 0)
            {
                throw new FileNotFoundException($"File {filename} is not enqueued for user {username}");
            }

            return globalPosition;
        }

        /// <summary>
        ///     Determines whether an upload slot is available for the specified <paramref name="username"/>.
        /// </summary>
        /// <param name="username">The username for which slot availability is to be checked.</param>
        /// <returns>A value indicating whether an upload slot is available for the specified username.</returns>
        public bool IsSlotAvailable(string username)
        {
            var group = Users.GetGroup(username);

            if (Groups.TryGetValue(group, out var record))
            {
                return record.SlotAvailable;
            }

            return false;
        }

        private void Configure(Options options)
        {
            int GetExistingUsedSlotsOrDefault(string group)
                => Groups.ContainsKey(group) ? Groups[group].UsedSlots : 0;

            SyncRoot.Wait();

            try
            {
                var optionsHash = Compute.Sha1Hash(options.Groups.ToJson());

                if (optionsHash == LastOptionsHash && options.Global.Upload.Slots == LastGlobalSlots)
                {
                    return;
                }

                MaxSlots = options.Global.Upload.Slots;

                // statically add built-in groups
                var groups = new List<Group>()
                {
                    // the priority group is hard-coded with priority 0, slot count equivalent to the overall max, and a FIFO
                    // strategy. all other groups have a minimum priority of 1 (enforced by options validation) to ensure that
                    // privileged users always take priority, regardless of user configuration. the strategy is fixed to FIFO
                    // because that gives privileged users the closest experience to the official client, as well as the
                    // appearance of fairness once the first upload begins.
                    new Group()
                    {
                        Name = Application.PrivilegedGroup,
                        Priority = 0,
                        Slots = MaxSlots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.PrivilegedGroup),
                        Strategy = QueueStrategy.FirstInFirstOut,
                    },
                    new Group()
                    {
                        Name = Application.DefaultGroup,
                        Priority = options.Groups.Default.Upload.Priority,
                        Slots = options.Groups.Default.Upload.Slots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.DefaultGroup),
                        Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), options.Groups.Default.Upload.Strategy, true),
                    },
                    new Group()
                    {
                        Name = Application.LeecherGroup,
                        Priority = options.Groups.Leechers.Upload.Priority,
                        Slots = options.Groups.Leechers.Upload.Slots,
                        UsedSlots = GetExistingUsedSlotsOrDefault(Application.LeecherGroup),
                        Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), options.Groups.Leechers.Upload.Strategy, true),
                    },
                };

                // dynamically add user-defined groups
                groups.AddRange(options.Groups.UserDefined.Select(kvp => new Group()
                {
                    Name = kvp.Key,
                    Priority = kvp.Value.Upload.Priority,
                    Slots = kvp.Value.Upload.Slots,
                    UsedSlots = GetExistingUsedSlotsOrDefault(kvp.Key),
                    Strategy = (QueueStrategy)Enum.Parse(typeof(QueueStrategy), kvp.Value.Upload.Strategy, true),
                }));

                Groups = groups.ToDictionary(g => g.Name);

                LastGlobalSlots = options.Global.Upload.Slots;
                LastOptionsHash = optionsHash;
            }
            finally
            {
                SyncRoot.Release();
                Process();
            }
        }

        private Upload Process()
        {
            SyncRoot.Wait();

            try
            {
                if (Groups.Values.Sum(g => g.UsedSlots) >= MaxSlots)
                {
                    return null;
                }

                // flip the uploads dictionary so that it is keyed by group instead of user. wait until just before we process the
                // queue to do this, and fetch each user's group as we do, to allow users to move between groups at run time. we
                // delay "pinning" an upload to a group (via UsedSlots, below) for the same reason.
                var readyUploadsByGroup = Uploads.Aggregate(
                    seed: new ConcurrentDictionary<string, List<Upload>>(),
                    func: (groups, user) =>
                    {
                        var ready = user.Value.Where(u => u.Ready.HasValue && !u.Started.HasValue);

                        if (ready.Any())
                        {
                            var group = Users.GetGroup(user.Key);

                            groups.AddOrUpdate(
                                key: group,
                                addValue: new List<Upload>(ready),
                                updateValueFactory: (group, list) =>
                                {
                                    list.AddRange(ready);
                                    return list;
                                });
                        }

                        return groups;
                    });

                // process each group in ascending order of priority, and stop after the first ready upload is released.
                foreach (var group in Groups.Values.OrderBy(g => g.Priority).ThenBy(g => g.Name))
                {
                    if (group.UsedSlots >= group.Slots || !readyUploadsByGroup.TryGetValue(group.Name, out var uploads) || !uploads.Any())
                    {
                        continue;
                    }

                    var upload = uploads
                        .OrderBy(u => group.Strategy == QueueStrategy.FirstInFirstOut ? u.Enqueued : u.Ready)
                        .First();

                    // mark the upload as started, and "pin" it to the group from which the slot is obtained, so the slot can be
                    // returned to the proper place upon completion
                    upload.Started = DateTime.UtcNow;
                    upload.Group = group.Name;
                    group.UsedSlots++;

                    // release the upload
                    upload.TaskCompletionSource.SetResult();
                    Log.Debug("Started: {File} for {User} at {Time}", Path.GetFileName(upload.Filename), upload.Username, upload.Enqueued);
                    Log.Debug("Group {Group} slots: {Used}/{Available}", group.Name, group.UsedSlots, group.Slots);

                    return upload;
                }

                return null;
            }
            finally
            {
                SyncRoot.Release();
            }
        }
    }
}