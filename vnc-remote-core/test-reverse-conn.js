const net = require('net');
const { exec } = require('child_process');

const LOCAL_IP = '10.7.132.163';
const PORT = 5500;

const server = net.createServer((sock) => {
  console.log('>>> CONNECTED! Peer address:', sock.remoteAddress);
  sock.on('data', (d) => {
    console.log('Received from tvnserver:', d.toString('utf8').trim());
    if (d.toString().startsWith('RFB 003.')) {
      console.log('Replying with client version RFB 003.008...');
      sock.write('RFB 003.008\n');
    } else {
      console.log('Post-handshake response (hex):', d.toString('hex'));
      console.log('Post-handshake response (text):', d.toString('utf8'));
      sock.destroy();
      server.close();
      process.exit(0);
    }
  });
});

server.listen(PORT, LOCAL_IP, () => {
  console.log(`Bridge listening on ${LOCAL_IP}:${PORT}. Sending -connect...`);
  exec(`"C:\\Program Files\\TightVNC\\tvnserver.exe" -controlservice -connect ${LOCAL_IP}:${PORT}`, (err, stdout, stderr) => {
    console.log('Exec result:', { err: err ? err.message : null, stdout, stderr });
  });
});

setTimeout(() => {
  console.log('Timeout waiting for tvnserver.');
  server.close();
  process.exit(1);
}, 6000);
