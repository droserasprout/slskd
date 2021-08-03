﻿// <copyright file="ConfigurationController.cs" company="slskd Team">
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

namespace slskd.Application.Management.API
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using slskd.Management;
    using slskd.Shares;

    /// <summary>
    ///     Configuration.
    /// </summary>
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0")]
    [ApiController]
    [Produces("application/json")]
    [Consumes("application/json")]
    public class ConfigurationController : ControllerBase
    {
        public ConfigurationController(
            IOptionsSnapshot<Options> optionsShapshot,
            IStateMonitor<SharedFileCacheState> sharedFileCacheMonitor,
            IManagementService managementService)
        {
            OptionsShapshot = optionsShapshot;
            SharedFileCacheMonitor = sharedFileCacheMonitor;
            ManagementService = managementService;
        }

        private IManagementService ManagementService { get; }
        private IOptionsSnapshot<Options> OptionsShapshot { get; }
        private IStateMonitor<SharedFileCacheState> SharedFileCacheMonitor { get; }

        [HttpGet]
        [Authorize]
        public IActionResult GetOptions()
        {
            // todo: sanitize this to remove passwords
            return Ok(OptionsShapshot.Value);
        }

        [HttpPut]
        [Authorize]
        public async Task<IActionResult> RescanSharesAsync()
        {
            try
            {
                await ManagementService.RescanSharesAsync();
            }
            catch (ShareScanInProgressException ex)
            {
                return Conflict(ex.Message);
            }

            return Ok();
        }
    }
}
