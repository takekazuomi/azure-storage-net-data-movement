//------------------------------------------------------------------------------
// <copyright file="MD5HashStream.cs" company="Microsoft">
//    Copyright (c) Microsoft Corporation
// </copyright>
//------------------------------------------------------------------------------
namespace Microsoft.WindowsAzure.Storage.DataMovement
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Class to make thread safe stream access and calculate MD5 hash.
    /// </summary>
    internal class MD5HashStream : IDisposable
    {
        /// <summary>
        /// Stream  object.
        /// </summary>
        private Stream stream;

        /// <summary>
        /// Semaphore object. In our case, we can only have one operation at the same time.
        /// </summary>
        private SemaphoreSlim semaphore;

        /// <summary>
        /// In restart mode, we start a separate thread to calculate MD5hash of transferred part.
        /// This variable indicates whether finished to calculate this part of MD5hash.
        /// </summary>
        private volatile bool finishedSeparateMd5Calculator = false;

        /// <summary>
        /// Indicates whether succeeded in calculating MD5hash of the transferred bytes.
        /// </summary>
        private bool succeededSeparateMd5Calculator = false;

        /// <summary>
        /// Running md5 hash of the blob being downloaded.
        /// </summary>
        private MD5CryptoServiceProvider md5hash;

        /// <summary>
        /// Offset of the transferred bytes. We should calculate MD5hash on all bytes before this offset.
        /// </summary>
        private long md5hashOffset;

        /// <summary>
        /// Initializes a new instance of the <see cref="MD5HashStream"/> class.
        /// </summary>
        /// <param name="stream">Stream object.</param>
        /// <param name="lastTransferOffset">Offset of the transferred bytes.</param>
        /// <param name="md5hashCheck">Whether need to calculate MD5Hash.</param>
        public MD5HashStream(
            Stream stream,
            long lastTransferOffset,
            bool md5hashCheck)
        {
            this.stream = stream;
            this.md5hashOffset = lastTransferOffset;

            if ((0 == this.md5hashOffset)
                || (!md5hashCheck))
            {
                this.finishedSeparateMd5Calculator = true;
                this.succeededSeparateMd5Calculator = true;
            }
            else
            {
                this.semaphore = new SemaphoreSlim(1, 1);
            }

            if (md5hashCheck)
            {
                this.md5hash = new MD5CryptoServiceProvider();
            }

            if ((!this.finishedSeparateMd5Calculator)
                && (!this.stream.CanRead))
            {
                throw new NotSupportedException(string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.StreamMustSupportReadException,
                    "Stream"));
            }

            if (!this.stream.CanSeek)
            {
                throw new NotSupportedException(string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.StreamMustSupportSeekException,
                    "Stream"));
            }
        }

        /// <summary>
        /// Gets a value indicating whether need to calculate MD5 hash.
        /// </summary>
        public bool CheckMd5Hash
        {
            get
            {
                return null != this.md5hash;
            }
        }

        /// <summary>
        /// Gets MD5 hash bytes.
        /// </summary>
        public byte[] Hash
        {
            get
            {
                return null == this.md5hash ? null : this.md5hash.Hash;
            }
        }

        /// <summary>
        /// Gets a value indicating whether already finished to calculate MD5 hash of transferred bytes.
        /// </summary>
        public bool FinishedSeparateMd5Calculator
        {
            get
            {
                return this.finishedSeparateMd5Calculator;
            }
        }

        /// <summary>
        /// Gets a value indicating whether already succeeded in calculating MD5 hash of transferred bytes.
        /// </summary>
        public bool SucceededSeparateMd5Calculator
        {
            get
            {
                this.WaitMD5CalculationToFinish();
                return this.succeededSeparateMd5Calculator;
            }
        }

        /// <summary>
        /// Calculate MD5 hash of transferred bytes.
        /// </summary>
        /// <param name="memoryManager">Reference to MemoryManager object to require buffer from.</param>
        /// <param name="checkCancellation">Action to check whether to cancel this calculation.</param>
        public void CalculateMd5(MemoryManager memoryManager, Action checkCancellation)
        {
            if (null == this.md5hash)
            {
                return;
            }

            byte[] buffer = null;

            try
            {
                buffer = Utils.RequireBuffer(memoryManager, checkCancellation);
            }
            catch (Exception)
            {
                lock (this.md5hash)
                {
                    this.finishedSeparateMd5Calculator = true;
                }

                throw;
            }

            long offset = 0;
            int readLength = 0;

            while (true)
            {
                lock (this.md5hash)
                {
                    if (offset >= this.md5hashOffset)
                    {
                        Debug.Assert(
                            offset == this.md5hashOffset,
                            "We should stop the separate calculator thread just at the transferred offset");

                        this.succeededSeparateMd5Calculator = true;
                        this.finishedSeparateMd5Calculator = true;
                        break;
                    }

                    readLength = (int)Math.Min(this.md5hashOffset - offset, buffer.Length);
                }

                try
                {
                    checkCancellation();
                    readLength = this.Read(offset, buffer, 0, readLength);

                    lock (this.md5hash)
                    {
                        this.md5hash.TransformBlock(buffer, 0, readLength, null, 0);
                    }
                }
                catch (Exception)
                {
                    lock (this.md5hash)
                    {
                        this.finishedSeparateMd5Calculator = true;
                    }

                    memoryManager.ReleaseBuffer(buffer);

                    throw;
                }

                offset += readLength;
            }

            memoryManager.ReleaseBuffer(buffer);
        }

        /// <summary>
        /// Begin async read from stream.
        /// </summary>
        /// <param name="readOffset">Offset in stream to read from.</param>
        /// <param name="buffer">The buffer to read the data into.</param>
        /// <param name="offset">The byte offset in buffer at which to begin writing data read from the stream.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <param name="cancellationToken">Token used to cancel the asynchronous reading.</param>
        /// <returns>A task that represents the asynchronous read operation. The value of the
        /// <c>TResult</c> parameter contains the total number of bytes read into the buffer.</returns>
        public async Task<int> ReadAsync(long readOffset, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await this.WaitOnSemaphoreAsync(cancellationToken);

            try
            {
                this.stream.Position = readOffset;

                return await this.stream.ReadAsync(
                    buffer,
                    offset,
                    count,
                    cancellationToken);
            }
            finally
            {
                this.ReleaseSemaphore();
            }
        }

        /// <summary>
        /// Begin async write to stream.
        /// </summary>
        /// <param name="writeOffset">Offset in stream to write to.</param>
        /// <param name="buffer">The buffer to write the data from.</param>
        /// <param name="offset">The byte offset in buffer from which to begin writing.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <param name="cancellationToken">Token used to cancel the asynchronous writing.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        public async Task WriteAsync(long writeOffset, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await this.WaitOnSemaphoreAsync(cancellationToken);

            try
            {
                this.stream.Position = writeOffset;
                await this.stream.WriteAsync(
                    buffer,
                    offset,
                    count,
                    cancellationToken);
            }
            finally
            {
                this.ReleaseSemaphore();
            }
        }

        /// <summary>
        /// Computes the hash value for the specified region of the input byte array
        /// and copies the specified region of the input byte array to the specified
        /// region of the output byte array.
        /// </summary>
        /// <param name="streamOffset">Offset in stream of the block on which to calculate MD5 hash.</param>
        /// <param name="inputBuffer">The input to compute the hash code for.</param>
        /// <param name="inputOffset">The offset into the input byte array from which to begin using data.</param>
        /// <param name="inputCount">The number of bytes in the input byte array to use as data.</param>
        /// <param name="outputBuffer">A copy of the part of the input array used to compute the hash code.</param>
        /// <param name="outputOffset">The offset into the output byte array from which to begin writing data.</param>
        /// <returns>Whether succeeded in calculating MD5 hash 
        /// or not finished the separate thread to calculate MD5 hash at the time. </returns>
        public bool MD5HashTransformBlock(long streamOffset, byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
        {
            if (null == this.md5hash)
            {
                return true;
            }

            if (!this.finishedSeparateMd5Calculator)
            {
                lock (this.md5hash)
                {
                    if (!this.finishedSeparateMd5Calculator)
                    {
                        if (streamOffset == this.md5hashOffset)
                        {
                            this.md5hashOffset += inputCount;
                        }

                        return true;
                    }
                    else
                    {
                        if (!this.succeededSeparateMd5Calculator)
                        {
                            return false;
                        }
                    }
                }
            }

            if (streamOffset >= this.md5hashOffset)
            {
                Debug.Assert(
                    this.finishedSeparateMd5Calculator,
                    "The separate thread to calculate MD5 hash should have finished or md5hashOffset should get updated.");

                this.md5hash.TransformBlock(inputBuffer, inputOffset, inputCount, outputBuffer, outputOffset);
            }

            return true;
        }

        /// <summary>
        /// Computes the hash value for the specified region of the specified byte array.
        /// </summary>
        /// <param name="inputBuffer">The input to compute the hash code for.</param>
        /// <param name="inputOffset">The offset into the byte array from which to begin using data.</param>
        /// <param name="inputCount">The number of bytes in the byte array to use as data.</param>
        /// <returns>An array that is a copy of the part of the input that is hashed.</returns>
        public byte[] MD5HashTransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
        {
            this.WaitMD5CalculationToFinish();

            if (!this.succeededSeparateMd5Calculator)
            {
                return null;
            }

            return null == this.md5hash ? null : this.md5hash.TransformFinalBlock(inputBuffer, inputOffset, inputCount);
        }

        /// <summary>
        /// Releases or resets unmanaged resources.
        /// </summary>
        public virtual void Dispose()
        {
            this.Dispose(true);
        }

        /// <summary>
        /// Private dispose method to release managed/unmanaged objects.
        /// If disposing = true clean up managed resources as well as unmanaged resources.
        /// If disposing = false only clean up unmanaged resources.
        /// </summary>
        /// <param name="disposing">Indicates whether or not to dispose managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (null != this.md5hash)
                {
                    this.md5hash.Clear();
                    this.md5hash = null;
                }

                if (null != this.semaphore)
                {
                    this.semaphore.Dispose();
                    this.semaphore = null;
                }
            }
        }

        /// <summary>
        /// Read from stream.
        /// </summary>
        /// <param name="readOffset">Offset in stream to read from.</param>
        /// <param name="buffer">An array of bytes. When this method returns, the buffer contains the specified
        ///  byte array with the values between offset and (offset + count - 1) replaced
        ///  by the bytes read from the current source.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin storing the data read from the current stream.</param>
        /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
        /// <returns>The total number of bytes read into the buffer.</returns>
        private int Read(long readOffset, byte[] buffer, int offset, int count)
        {
            if (!this.finishedSeparateMd5Calculator)
            {
                this.semaphore.Wait();
            }

            try
            {
                this.stream.Position = readOffset;
                int readBytes = this.stream.Read(buffer, offset, count);

                return readBytes;
            }
            finally
            {
                this.ReleaseSemaphore();
            }
        }

        /// <summary>
        /// Wait for one semaphore.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel waiting on the semaphore.</param>
        private async Task WaitOnSemaphoreAsync(CancellationToken cancellationToken)
        {
            if (!this.finishedSeparateMd5Calculator)
            {
                await this.semaphore.WaitAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Release semaphore.
        /// </summary>
        private void ReleaseSemaphore()
        {
            if (!this.finishedSeparateMd5Calculator)
            {
                this.semaphore.Release();
            }
        }

        /// <summary>
        /// Wait for MD5 calculation to be finished.
        /// In our test, MD5 calculation is really fast, 
        /// and SpinOnce has sleep mechanism, so use Spin instead of sleep here.
        /// </summary>
        private void WaitMD5CalculationToFinish()
        {
            if (this.finishedSeparateMd5Calculator)
            {
                return;
            }

            SpinWait sw = new SpinWait();

            while (!this.finishedSeparateMd5Calculator)
            {
                sw.SpinOnce();
            }

            sw.Reset();
        }
    }
}
