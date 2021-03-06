# sds_type.py
#
# Copyright 2019 OSIsoft, LLC
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
# <http://www.apache.org/licenses/LICENSE-2.0>
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

from sds_type_code import SdsTypeCode
from sds_type_property import SdsTypeProperty
import json


class SdsType(object):
    def __init__(self):
        self.SdsTypeCode = SdsTypeCode.Empty

    @property
    def Id(self):
        return self.__id
    @Id.setter
    def Id(self, id):
        self.__id = id

    @property
    def Name(self):
        return self.__name
    @Name.setter
    def Name(self, name):
        self.__name = name

    @property
    def Properties(self):
        return self.__properties
    @Properties.setter
    def Properties(self, properties):
        self.__properties = properties

    @property
    def Description(self):
        return self.__description
    @Description.setter
    def Description(self, description):
        self.__description = description

    @property
    def BaseType(self):
        return self.__baseType
    @BaseType.setter
    def BaseType(self, baseType):
        self.__baseType = baseType

    @property
    def SdsTypeCode(self):
        return self.__typeCode
    @SdsTypeCode.setter
    def SdsTypeCode(self, typeCode):
        self.__typeCode = typeCode

    def to_json(self):
        return json.dumps(self.to_dictionary())

    def to_dictionary(self):
        dictionary = {'SdsTypeCode': self.SdsTypeCode.value}

        if hasattr(self, 'Properties'):
            dictionary['Properties'] = []
            for prop in self.Properties:
                dictionary['Properties'].append(prop.to_dictionary())

        if hasattr(self, 'Id'):
            dictionary['Id'] = self.Id

        if hasattr(self, 'Name'):
            dictionary['Name'] = self.Name

        if hasattr(self, 'Description'):
            dictionary['Description'] = self.Description

        if hasattr(self, 'BaseType'):
            dictionary['BaseType'] = self.BaseType.to_dictionary()

        return dictionary

    @staticmethod
    def from_json(json_obj):
        return SdsType.from_dictionary(json_obj)

    @staticmethod
    def from_dictionary(content):
        event_type = SdsType()

        if len(content) == 0:
            return event_type

        if 'Id' in content:
            event_type.Id = content['Id']

        if 'Name' in content:
            event_type.name = content['Name']

        if 'Description' in content:
            event_type.description = content['Description']

        if 'SdsTypeCode' in content:
            event_type.SdsTypeCode = SdsTypeCode(content['SdsTypeCode'])

        if 'BaseType' in content:
            event_type.BaseType = SdsType.from_dictionary(content['BaseType'])

        if 'Properties' in content:
            properties = content['Properties']
            if properties is not None and len(properties) > 0:
                event_type.Properties = []
                for prop in properties:
                    event_type.Properties.append(SdsTypeProperty.from_dictionary(prop))
        return event_type
