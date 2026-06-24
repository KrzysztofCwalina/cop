class BlobClient {
  download(path) {
    console.log(`downloading ${path}`);
    try {
      this._process(path);
    } catch (e) {
      // swallowed
    }
  }

  _process(path) {
    const x = 1;
    const y = 2;
    console.log(x + y);
  }
}
