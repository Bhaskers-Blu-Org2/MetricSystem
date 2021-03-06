﻿// The MIT License (MIT)
// 
// Copyright (c) 2015 Microsoft
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace MetricSystem.Configuration
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Represents configuration data backed by one or more sources. Sources are merged in order and updates are
    /// posted as they occur. Sources are disposed when the data is disposed.
    /// </summary>
    public sealed class ConfigurationData<T> : IDisposable
        where T : ConfigurationBase
    {
        /// <summary>
        /// Method to handle update events.
        /// </summary>
        /// <param name="newDataIsValid">True if the latest data provided is valid.</param>
        public delegate void OnUpdated(ConfigurationData<ConfigurationBase> source, bool newDataIsValid);

        /// <summary>
        /// Called when parsing data yields an unhandled exception.
        /// </summary>
        /// <param name="source">Source of the data.</param>
        /// <param name="ex">The exception.</param>
        public delegate void OnUnhandledException(ConfigurationData<ConfigurationBase> source, Exception ex);

        private static readonly JsonMergeSettings MergeSettings = new JsonMergeSettings
                                                                  {
                                                                      MergeArrayHandling = MergeArrayHandling.Replace
                                                                  };

        private readonly List<ConfigurationSource> sources;
        private T value;

        /// <summary>
        /// Create a new configuration data object backed by one or more sources.
        /// </summary>
        /// <param name="sources">Collection of sources to use.</param>
        public ConfigurationData(ICollection<ConfigurationSource> sources)
        {
            if (sources.Count < 1)
            {
                throw new ArgumentException("Must provide at least one source.", "sources");
            }

            this.sources = new List<ConfigurationSource>(sources);
            foreach (var s in this.sources)
            {
                if (s == null)
                {
                    throw new ArgumentException("Individual sources may not be null.", "sources");
                }
                s.SourceUpdated += this.BuildValueData;
            }

            this.BuildValueData();
        }

        /// <summary>
        /// The last valid data object (null if no valid data has been provided).
        /// </summary>
        public T Value
        {
            get { return this.value; }
        }

        /// <summary>
        /// Whether the last update to occur produced valid data.
        /// </summary>
        public bool IsValid { get; private set; }

        public void Dispose()
        {
            foreach (var s in this.sources)
            {
                s.Dispose();
            }

            this.sources.Clear();
        }

        private void BuildValueData()
        {
            var objects = new JObject[this.sources.Count];
            var isValid = true;
            for (var i = 0; i < this.sources.Count; ++i)
            {
                try
                {
                    objects[i] = this.sources[i].Read();
                }
                catch (JsonException ex)
                {
                    isValid = false;
                    Events.Write.Error(this.sources[i].ToString(),
                                       string.Format("Exception parsing JSON: {0}\nStack: {1}", ex.Message,
                                                     ex.StackTrace));
                }
            }

            if (isValid)
            {
                for (var i = 1; i < objects.Length; ++i)
                {
                    objects[0].Merge(objects[i], MergeSettings);
                }

                try
                {
                    using (var reader = new JTokenReader(objects[0]))
                    {
                        var serializer = new JsonSerializer();
                        var newValue = serializer.Deserialize<T>(reader);

                        newValue.__Sources__ = sources;
                        isValid = newValue.Validate();
                        newValue.__Sources__ = null;

                        if (isValid)
                        {
                            Interlocked.Exchange(ref this.value, newValue);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Events.Write.Error(this.sources,
                                       string.Format("Exception parsing JSON: {0}\nStack: {1}", ex.Message,
                                                     ex.StackTrace));
                    isValid = false;
                }
                catch (Exception ex)
                {
                    if (this.UnhandledException != null)
                    {
                        this.UnhandledException(this as ConfigurationData<ConfigurationBase>, ex);
                    }

                    throw;
                }
            }

            this.IsValid = isValid;
            if (this.Updated != null)
            {
                this.Updated(this as ConfigurationData<ConfigurationBase>, this.IsValid);
            }
        }

        /// <summary>
        /// Triggered whenever underlying sources are updated.
        /// </summary>
        public event OnUpdated Updated;

        /// <summary>
        /// Triggered when attempting to process data yields an unhandled exception.
        /// </summary>
        public event OnUnhandledException UnhandledException;

        ~ConfigurationData()
        {
            this.Dispose();
        }
    }
}
