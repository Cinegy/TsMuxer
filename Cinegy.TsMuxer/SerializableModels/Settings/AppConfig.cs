/* Copyright 2023 Cinegy GmbH.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

namespace Cinegy.TsMuxer.SerializableModels.Settings
{
    public class AppConfig
    {
        public string Ident { get; set; } = "Muxer1";

        public string Label { get; set; }

        public string VideoSourceUrl { get; set; }

        public string KlvSourceUrl { get; set; }

        public string OutputUrl { get; set; }

        public MetricsSetting Metrics { get; set; } = new MetricsSetting();

    }
}
