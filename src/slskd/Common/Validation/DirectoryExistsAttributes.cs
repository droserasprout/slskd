﻿// <copyright file="DirectoryExistsAttributes.cs" company="slskd Team">
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

namespace slskd.Validation
{
    using System;
    using System.ComponentModel.DataAnnotations;
    using System.IO;
    using System.Linq;

    /// <summary>
    ///     Validates that the directory at the specified path exists.
    /// </summary>
    public class DirectoryExistsAttribute : ValidationAttribute
    {
        public DirectoryExistsAttribute(bool ensureWriteable = false)
        {
            EnsureWriteable = ensureWriteable;
        }

        private bool EnsureWriteable { get; set; }

        protected override ValidationResult IsValid(object value, ValidationContext validationContext)
        {
            if (value != null)
            {
                var dir = Path.GetFullPath(value?.ToString());

                // default directories are exempt from validation, as there's an additional check (and creation)
                // at startup for these directories.
                if (new[] { Program.DefaultDownloadsDirectory, Program.DefaultIncompleteDirectory }.Contains(dir))
                {
                    return ValidationResult.Success;
                }

                // empty values are valid, because they will fall back to defaults
                if (string.IsNullOrEmpty(dir))
                {
                    return ValidationResult.Success;
                }

                if (!Directory.Exists(dir))
                {
                    return new ValidationResult($"The {validationContext.DisplayName} field specifies a non-existent directory '{dir}'.");
                }

                if (EnsureWriteable)
                {
                    try
                    {
                        var file = Guid.NewGuid().ToString();
                        var probe = Path.Combine(dir, file);
                        File.WriteAllText(probe, string.Empty);
                        File.Delete(probe);
                    }
                    catch (Exception)
                    {
                        return new ValidationResult($"The {validationContext.DisplayName} field specifies a directory '{dir}' that exists, but is not writeable.");
                    }
                }
            }

            return ValidationResult.Success;
        }
    }
}