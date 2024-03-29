﻿using System;
using System.Threading.Tasks;

namespace FHSDK.Droid
{
    /// <summary>
    /// Contains the entry class of the FeedHenry Xamarin SDK for Android platform. It's defined in the FHXamarinAndroidSDK.dll.
    /// To use the FeedHenry SDK, both FHSDK.dll and FHXamarinAndroidSDK.dll should be referenced by your Xamarain Android project, and initialise the SDK using the FHClient class in this name space.
    /// </summary>
    [System.Runtime.CompilerServices.CompilerGenerated]
    class NamespaceDoc
    {

    }

	public class FHClient: FH
	{
        /// <summary>
        /// Initialise the FeedHenry SDK. This should be called before any other API functions are invoked. Usually this should be called after the app finish intialising.
        /// </summary>
        /// <example>
        /// <code>
        ///  protected async override void OnCreate (Bundle bundle)
        ///  {
        ///    //other initialisation work
        ///    await  FHClient.Init();
        ///  }
        /// </code>
        /// </example>
        /// <returns>If Init is success or not</returns>
        /// <exception cref="FHException"></exception>
		public new static async Task<bool> Init()
		{
			return await FH.Init ();
		}
	}
}


