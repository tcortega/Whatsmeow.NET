package media

// openNonBlocking is nothing on Windows: its named pipes are in their own namespace (\\.\pipe\), never a file under
// a directory, so opening a path under the root does not wait for a writer [inferred].
const openNonBlocking = 0
