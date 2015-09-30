﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Elasticsearch.Net.Connection;
using Elasticsearch.Net.Serialization;

namespace Nest
{
	public class Metrics : IUrlParameter
	{
		public string GetString(IConnectionConfigurationValues settings)
		{
			throw new NotImplementedException();
		}
	}

	public class Features : IUrlParameter
	{
		public string GetString(IConnectionConfigurationValues settings)
		{
			throw new NotImplementedException();
		}
	}

	public class Feature : IUrlParameter
	{
		public string GetString(IConnectionConfigurationValues settings)
		{
			throw new NotImplementedException();
		}
	}



	public class PropertyNames : IUrlParameter
	{
		public string GetString(IConnectionConfigurationValues settings)
		{
			throw new NotImplementedException();
		}
		}

	public class FieldNames : IUrlParameter
	{
		private readonly string _name;
		public FieldNames(string name)
		{
			this._name = name;
		}

		public string GetString(IConnectionConfigurationValues settings) => this._name;
		public static implicit operator FieldNames(string[] fields)
		{
			throw new NotImplementedException();
		}
	}

}